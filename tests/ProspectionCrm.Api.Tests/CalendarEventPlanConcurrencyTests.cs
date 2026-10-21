using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ProspectionCrm.Api.Tests;

public sealed class CalendarEventPlanConcurrencyTests(AutomationJobDatabase database) : CalendarEventPlanFixture(database), IClassFixture<AutomationJobDatabase>
{
    [Theory]
    [InlineData("approve", "approve")][InlineData("approve", "reject")][InlineData("cancel", "approve")]
    public async Task CompetingDecisionsHaveOneConsistentWinner(string first, string second)
    {
        using var s = await Prepare(); var p = await Submit(s, await Create(s));
        Task<HttpResponseMessage> Act(string action) => action == "cancel"
            ? s.Client.PostAsJsonAsync($"/api/calendar-event-plans/{p.Id}/cancel", new { revision = p.Revision })
            : Decide(s, p, action == "approve");
        var results = await Task.WhenAll(Act(first), Act(second));
        Assert.All(results, r => Assert.Contains(r.StatusCode, new[] { HttpStatusCode.OK, HttpStatusCode.Conflict }));
        Assert.Equal(first == second ? 2 : 1, results.Count(r => r.StatusCode == HttpStatusCode.OK));
        var current = await Read(s, p.Id); await using var db = Db();
        Assert.Equal(current.StatusCode, (await db.AutomationActionRequests.SingleAsync(x => x.Id == p.AutomationActionRequestId)).StatusCode);
        Assert.Equal(2, current.Revision); await AssertNoEffects(s);
    }
    [Fact]
    public async Task UpdateVersusSubmitNeverApprovesAnUnseenRevision()
    {
        using var s = await Prepare(); var p = await Create(s);
        var results = await Task.WhenAll(
            s.Client.PutAsJsonAsync($"/api/calendar-event-plans/{p.Id}", new { revision = p.Revision, content = Content(s) with { Title = "Replacement" } }),
            s.Client.PostAsJsonAsync($"/api/calendar-event-plans/{p.Id}/submit", new { revision = p.Revision }));
        Assert.Single(results, r => r.StatusCode == HttpStatusCode.OK); Assert.Single(results, r => r.StatusCode == HttpStatusCode.Conflict);
        var current = await Read(s, p.Id); Assert.Equal(1, current.Revision);
        if (current.StatusCode == "pending-approval") Assert.Equal(p.Content.Content.Title, current.Content.Content.Title);
        else { Assert.Equal("draft", current.StatusCode); Assert.Null(current.AutomationActionRequestId); Assert.Equal("Replacement", current.Content.Content.Title); }
        await AssertNoEffects(s);
    }
    [Fact]
    public async Task DoubleSubmitCreatesOnlyOneRequest()
    {
        using var s = await Prepare(); var p = await Create(s);
        var responses = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => s.Client.PostAsJsonAsync($"/api/calendar-event-plans/{p.Id}/submit", new { revision = 0 })));
        Assert.Single(responses, x => x.StatusCode == HttpStatusCode.OK);
        Assert.Single(responses, x => x.StatusCode == HttpStatusCode.Conflict);
        await using var db = Db(); Assert.Single(await db.AutomationActionRequests.Where(x => x.WorkspaceId == s.Workspace).ToListAsync());
    }
    [Fact]
    public async Task DisableCommittedWhileApprovalWaitsOnAccountLockPreventsApproval()
    {
        using var s = await Prepare(); var p = await Submit(s, await Create(s));
        await using var db = Db(); await using var tx = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            SELECT 1 FROM "CalendarAccounts" WHERE "Id" = {s.Account.Id} FOR UPDATE;
            UPDATE "Calendars" SET "StatusCode" = 'disabled', "Revision" = "Revision" + 1 WHERE "Id" = {s.Calendar.Id}
            """);
        var decision = Decide(s, p); Assert.False(decision.IsCompleted);
        await tx.CommitAsync(); Assert.Equal(HttpStatusCode.Conflict, (await decision.WaitAsync(TimeSpan.FromSeconds(30))).StatusCode);
        Assert.Equal("pending-approval", (await Read(s, p.Id)).StatusCode); await AssertNoEffects(s);
    }
}
