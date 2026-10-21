using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;
using ProspectionCrm.Api.Dtos.AutomationActionRequests;
using ProspectionCrm.Api.Dtos.CalendarEvents;
using ProspectionCrm.Api.Services.Calendar;
using Xunit;

namespace ProspectionCrm.Api.Tests;

public sealed class CalendarEventPlanTests(AutomationJobDatabase database) : CalendarEventPlanFixture(database), IClassFixture<AutomationJobDatabase>
{
    [Fact]
    public async Task ApproveFreezesExactSnapshotAndNeverCreatesASendOrMessage()
    {
        using var s = await Prepare(); var draft = await Create(s); var p = await Submit(s, draft);
        Assert.Equal("pending-approval", p.StatusCode); Assert.Equal(draft.ContentHash, p.ContentHash);
        var request = await s.Client.GetFromJsonAsync<AutomationActionRequestDto>($"/api/automation-action-requests/{p.AutomationActionRequestId}");
        Assert.Equal("manual", request!.DecisionRequirementCode); Assert.False(request.IsStale);
        Assert.Null(request.AutomationJobId); Assert.Null(request.AutomationRuleId); Assert.Null(request.ActionPlan);
        Assert.Equal(p.ContentHash, request.ContentFingerprint); Assert.Equal(p.ContentHash, CalendarEventSnapshots.Hash(request.CalendarPlan!));
        Assert.Equal(HttpStatusCode.OK, (await Decide(s, p)).StatusCode);
        var approved = await Read(s, p.Id); Assert.Equal("approved", approved.StatusCode); Assert.NotNull(approved.ApprovedAt);
        Assert.Equal(draft.ContentHash, approved.ContentHash); Assert.Equal(p.ContentHash, CalendarEventSnapshots.Hash(approved.Content));
        var repeat = await Decide(s, p); Assert.Equal(HttpStatusCode.OK, repeat.StatusCode);
        Assert.Equal(JsonSerializer.Serialize(approved), JsonSerializer.Serialize(await Read(s, p.Id)));
        Assert.Equal(HttpStatusCode.Conflict, (await Decide(s, p, false)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await s.Client.PutAsJsonAsync($"/api/calendar-event-plans/{p.Id}", new UpdateCalendarEventPlanRequest(approved.Revision, Content(s)))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await s.Client.PostAsJsonAsync($"/api/calendar-event-plans/{p.Id}/cancel", new { revision = approved.Revision })).StatusCode);
        await AssertNoEffects(s);
    }
    [Fact]
    public async Task RejectAndRepeatAreTerminalWithoutEffects()
    {
        using var s = await Prepare(); var p = await Submit(s, await Create(s));
        Assert.Equal(HttpStatusCode.OK, (await Decide(s, p, false)).StatusCode);
        var rejected = await Read(s, p.Id); Assert.Equal("rejected", rejected.StatusCode); Assert.NotNull(rejected.RejectedAt);
        Assert.Equal(HttpStatusCode.OK, (await Decide(s, p, false)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Decide(s, p)).StatusCode);
        Assert.Equal(JsonSerializer.Serialize(rejected), JsonSerializer.Serialize(await Read(s, p.Id))); await AssertNoEffects(s);
    }
    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task CancelDraftOrPendingInvalidatesAnyDecision(bool pending)
    {
        using var s = await Prepare(); var p = await Create(s); if (pending) p = await Submit(s, p);
        Assert.Equal(HttpStatusCode.OK, (await s.Client.PostAsJsonAsync($"/api/calendar-event-plans/{p.Id}/cancel", new { revision = p.Revision })).StatusCode);
        var cancelled = await Read(s, p.Id); Assert.Equal("cancelled", cancelled.StatusCode); Assert.NotNull(cancelled.CancelledAt);
        if (pending) Assert.Equal(HttpStatusCode.Conflict, (await Decide(s, p)).StatusCode);
        await AssertNoEffects(s);
    }
    [Fact]
    public async Task DuplicateSubmitAndStaleRevisionDoNotCreateAnotherRequest()
    {
        using var s = await Prepare(); var draft = await Create(s); var pending = await Submit(s, draft);
        foreach (var revision in new[] { draft.Revision, pending.Revision })
            Assert.Equal(HttpStatusCode.Conflict, (await s.Client.PostAsJsonAsync($"/api/calendar-event-plans/{draft.Id}/submit", new { revision })).StatusCode);
        await using var db = Db(); Assert.Single(await db.AutomationActionRequests.Where(x => x.WorkspaceId == s.Workspace).ToListAsync());
        Assert.Equal(HttpStatusCode.Conflict, (await s.Client.PutAsJsonAsync($"/api/calendar-event-plans/{draft.Id}", new UpdateCalendarEventPlanRequest(pending.Revision, Content(s)))).StatusCode);
    }
    [Fact]
    public async Task KillSwitchBlocksPreparationButNotAnAlreadyPendingHumanDecision()
    {
        using var s = await Prepare(); var pending = await Submit(s, await Create(s)); var draft = await Create(s);
        await using var db = Db(); var settings = await db.AutomationRuntimeSettings.SingleAsync(x => x.WorkspaceId == s.Workspace);
        settings.IsEnabled = false; await db.SaveChangesAsync();
        Assert.Equal(HttpStatusCode.Conflict, (await s.Client.PostAsJsonAsync($"/api/calendar-event-plans/{draft.Id}/submit", new { revision = draft.Revision })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Decide(s, pending)).StatusCode); await AssertNoEffects(s);
    }
    [Fact]
    public async Task FingerprintRevalidationAndDatabaseSnapshotImmutability()
    {
        using var s = await Prepare(); var p = await Submit(s, await Create(s)); await using var db = Db();
        var error = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "CalendarEventPlans" SET "ContentJson" = jsonb_build_object() WHERE "Id" = {p.Id}
            """)); Assert.Equal("23514", error.SqlState);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "AutomationActionRequests" SET "ActionPlanJson" = jsonb_set("ActionPlanJson", ARRAY['content','title'], '"tampered"')
            WHERE "Id" = {p.AutomationActionRequestId}
            """);
        Assert.Equal(HttpStatusCode.Conflict, (await Decide(s, p)).StatusCode);
        var request = await s.Client.GetFromJsonAsync<AutomationActionRequestDto>($"/api/automation-action-requests/{p.AutomationActionRequestId}");
        Assert.True(request!.IsStale); Assert.Null(request.CalendarPlan); Assert.Equal("pending", request.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Decide(s, p, false)).StatusCode);
    }
    [Fact]
    public async Task DatabaseRetainsDraftAndTerminalHistory()
    {
        using var s = await Prepare(); var p = await Create(s); await using var db = Db();
        Assert.Equal("23514", (await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"""
            DELETE FROM "CalendarEventPlans" WHERE "Id" = {p.Id}
            """))).SqlState);
        p = await Submit(s, p); Assert.Equal(HttpStatusCode.OK, (await Decide(s, p)).StatusCode);
        Assert.Equal("23514", (await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "CalendarEventPlans" SET "Revision" = "Revision" + 1 WHERE "Id" = {p.Id}
            """))).SqlState);
        await Assert.ThrowsAsync<PostgresException>(() => Microsoft.EntityFrameworkCore.Infrastructure.AccessorExtensions.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>(db)
            .MigrateAsync(CalendarSyncMigrationTests.Current));
        Assert.Equal(CalendarEventPlanMigrationTests.Current, (await db.Database.GetAppliedMigrationsAsync()).Last());
    }
}
