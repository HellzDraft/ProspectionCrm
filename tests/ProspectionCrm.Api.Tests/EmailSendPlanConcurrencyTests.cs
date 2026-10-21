using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ProspectionCrm.Api.Tests;

public sealed class EmailSendPlanConcurrencyTests(AutomationJobDatabase database) : EmailSendPlanFixture(database), IClassFixture<AutomationJobDatabase>
{
    [Theory]
    [InlineData("approve", "approve")][InlineData("approve", "reject")][InlineData("cancel", "approve")]
    public async Task CompetingDecisionsHaveOneConsistentWinner(string first, string second)
    {
        using var s = await Prepare(); var p = await Submit(s, await Create(s));
        Task<HttpResponseMessage> Act(string action) => action == "cancel"
            ? s.Client.PostAsJsonAsync($"/api/email-send-plans/{p.Id}/cancel", new { revision = p.Revision })
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
            s.Client.PutAsJsonAsync($"/api/email-send-plans/{p.Id}", new { revision = p.Revision, content = Content(s) with { BodyText = "Replacement" } }),
            s.Client.PostAsJsonAsync($"/api/email-send-plans/{p.Id}/submit", new { revision = p.Revision }));
        Assert.Single(results, r => r.StatusCode == HttpStatusCode.OK); Assert.Single(results, r => r.StatusCode == HttpStatusCode.Conflict);
        var current = await Read(s, p.Id); Assert.Equal(1, current.Revision);
        if (current.StatusCode == "pending-approval") Assert.Equal(p.Content.BodyText, current.Content.BodyText);
        else { Assert.Equal("draft", current.StatusCode); Assert.Null(current.AutomationActionRequestId); Assert.Equal("Replacement", current.Content.BodyText); }
        await AssertNoEffects(s);
    }
}
