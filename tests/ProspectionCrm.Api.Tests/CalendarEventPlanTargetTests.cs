using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ProspectionCrm.Api.Entities;
using ProspectionCrm.Api.Dtos.CalendarEvents;
using Xunit;
namespace ProspectionCrm.Api.Tests;
public sealed class CalendarEventPlanTargetTests(AutomationJobDatabase database) : CalendarEventPlanFixture(database), IClassFixture<AutomationJobDatabase>
{
    private async Task<CalendarEvent> Seed(Scenario s)
    {
        await using var db = Db();
        var e = new CalendarEvent { WorkspaceId = s.Workspace, CalendarId = s.Calendar.Id, ProviderCode = "synthetic",
            ExternalEventId = "occurrence", ExternalRevision = 3, ExternalContentSha256 = new string('a', 64),
            Title = "Imported", StartsAt = DateTimeOffset.Parse("2026-10-21T08:00:00Z"), EndsAt = DateTimeOffset.Parse("2026-10-21T09:00:00Z"),
            TimeZoneId = "Europe/Paris", StatusCode = "confirmed", SeriesId = "series", OccurrenceId = "stable-occurrence" };
        db.Add(e); await db.SaveChangesAsync(); return e;
    }
    [Fact]
    public async Task UpdateApprovalPreservesImportedEventAndOccurrenceExactly()
    {
        using var s = await Prepare(); var e = await Seed(s);
        await using var db = Db(); var before = JsonSerializer.Serialize(await db.CalendarEvents.AsNoTracking().SingleAsync(x => x.Id == e.Id));
        var p = await Submit(s, await Create(s, Content(s) with { OperationCode = "update", TargetCalendarEventId = e.Id }));
        Assert.Equal("stable-occurrence", p.Content.Target!.OccurrenceId); Assert.Equal(3, p.Content.Target.Revision);
        Assert.Equal(HttpStatusCode.OK, (await Decide(s, p)).StatusCode);
        Assert.Equal(before, JsonSerializer.Serialize(await db.CalendarEvents.AsNoTracking().SingleAsync(x => x.Id == e.Id)));
        Assert.Empty(await db.AutomationJobs.Where(x => x.WorkspaceId == s.Workspace).ToListAsync());
        Assert.Empty(await db.IntegrationOperations.Where(x => x.WorkspaceId == s.Workspace).ToListAsync()); Assert.Equal(0, s.Network.Calls);
    }
    [Theory]
    [InlineData(false, "revision")][InlineData(true, "revision")][InlineData(false, "cancelled")][InlineData(true, "cancelled")]
    [InlineData(true, "title")][InlineData(true, "occurrence")]
    public async Task ChangedTargetRefusesSubmissionOrApprovalAtomically(bool pending, string change)
    {
        using var s = await Prepare(); var e = await Seed(s);
        var p = await Create(s, Content(s) with { OperationCode = "update", TargetCalendarEventId = e.Id });
        if (pending) p = await Submit(s, p);
        await using var db = Db(); var target = await db.CalendarEvents.SingleAsync(x => x.Id == e.Id);
        if (change == "revision") target.ExternalRevision++;
        else if (change == "cancelled") target.StatusCode = "cancelled";
        else if (change == "title") target.Title = "Changed externally";
        else target.OccurrenceId = "changed";
        await db.SaveChangesAsync();
        var result = pending ? await Decide(s, p) : await s.Client.PostAsJsonAsync($"/api/calendar-event-plans/{p.Id}/submit", new { revision = p.Revision });
        Assert.Equal(HttpStatusCode.Conflict, result.StatusCode); Assert.Equal(p.StatusCode, (await Read(s, p.Id)).StatusCode);
        if (pending) Assert.Equal("pending", (await db.AutomationActionRequests.SingleAsync(x => x.Id == p.AutomationActionRequestId)).StatusCode);
    }
    [Theory]
    [InlineData(false, false)][InlineData(false, true)][InlineData(true, false)][InlineData(true, true)]
    public async Task DisabledCalendarOrAccountRefusesDecision(bool pending, bool account)
    {
        using var s = await Prepare(); var p = await Create(s); if (pending) p = await Submit(s, p);
        await using var db = Db();
        if (account) (await db.CalendarAccounts.SingleAsync(x => x.Id == s.Account.Id)).StatusCode = "disabled";
        else (await db.Calendars.SingleAsync(x => x.Id == s.Calendar.Id)).StatusCode = "disabled";
        await db.SaveChangesAsync();
        var result = pending ? await Decide(s, p) : await s.Client.PostAsJsonAsync($"/api/calendar-event-plans/{p.Id}/submit", new { revision = p.Revision });
        Assert.Equal(HttpStatusCode.Conflict, result.StatusCode); Assert.Equal(p.StatusCode, (await Read(s, p.Id)).StatusCode); await AssertNoEffects(s);
    }
    [Fact]
    public async Task DraftRefreshRequiresExplicitRevisionAndNewContent()
    {
        using var s = await Prepare(); var p = await Create(s);
        var response = await s.Client.PutAsJsonAsync($"/api/calendar-event-plans/{p.Id}", new UpdateCalendarEventPlanRequest(0, Content(s) with { Title = "Revised" }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var changed = (await response.Content.ReadFromJsonAsync<CalendarEventPlanDto>())!;
        Assert.Equal(1, changed.Revision); Assert.NotEqual(p.ContentHash, changed.ContentHash);
        Assert.Equal(HttpStatusCode.Conflict, (await s.Client.PostAsJsonAsync($"/api/calendar-event-plans/{p.Id}/submit", new { revision = 0 })).StatusCode);
        await Submit(s, changed); await AssertNoEffects(s);
    }
}
