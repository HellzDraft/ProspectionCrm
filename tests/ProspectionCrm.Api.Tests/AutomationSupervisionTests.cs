using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using ProspectionCrm.Api.Dtos.AutomationActionRequests;
using ProspectionCrm.Api.Dtos.AutomationSupervision;
using ProspectionCrm.Api.Entities;
using ProspectionCrm.Api.Services.Automation;
using Xunit;

namespace ProspectionCrm.Api.Tests;

public sealed class AutomationSupervisionTests(AutomationJobDatabase database)
    : AutomationSupervisionFixture(database), IClassFixture<AutomationJobDatabase>
{
    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task EmptySnapshotReflectsConfigurationWithoutClaimingHealth(bool enabled)
    {
        var workspace = await Workspace(); Config.Enabled = enabled;
        var snapshot = await Supervise(workspace);
        Assert.Equal(Clock.Now, snapshot.GeneratedAt); Assert.Equal(workspace, snapshot.WorkspaceId);
        Assert.Equal(enabled, snapshot.Worker.ConfiguredEnabled);
        Assert.Equal(Config.MaxAttempts, snapshot.Worker.MaxAttempts);
        Assert.Equal(Config.IdleDelaySeconds, snapshot.Worker.IdleDelaySeconds);
        Assert.Equal(Config.RecoveryIntervalSeconds, snapshot.Worker.RecoveryIntervalSeconds);
        Assert.Equal("manual", snapshot.RuntimeSettings.OperatingModeCode); Assert.False(snapshot.RuntimeSettings.IsEnabled);
        Assert.Equal(new AutomationQueueSummary(0,0,0,0,0,0,0,0,null,null,null,null), snapshot.Queue);
        Assert.Equal(new AutomationRequestSummary(0,0,0,0,null,null), snapshot.ActionRequests);
        Assert.Equal(new AutomationExecutionSummary(0,0,0,0,0,0,0,0,null,null), snapshot.Executions);
        Assert.Equal(0, snapshot.Quotas.Minute.Used); Assert.False(snapshot.Quotas.Day.IsReached);
        Assert.Equal("closed", snapshot.Circuit.StatusCode); Assert.Empty(snapshot.Alerts);
    }

    [Fact]
    public async Task QueueCountersUseApplicationWindowsAndPostgresLeaseClockWithoutMutation()
    {
        var ws = await Workspace(); var now = Clock.Now;
        await using var db = Db();
        var pgNow = await db.Database.SqlQuery<DateTimeOffset>($"SELECT statement_timestamp() AS \"Value\"").SingleAsync();
        var states = new[] { "pending", "pending", "leased", "leased", "awaiting-approval", "failed", "cancelled", "completed", "completed" };
        for (var i = 0; i < states.Length; i++)
        {
            var job = await Add(ws); var state = states[i]; var index = i;
            await db.AutomationJobs.Where(x => x.Id == job.Id).ExecuteUpdateAsync(s => s
                .SetProperty(x => x.StatusCode, state).SetProperty(x => x.CreatedAt, now.AddHours(-2).AddMinutes(index))
                .SetProperty(x => x.UpdatedAt, now.AddMinutes(-index))
                .SetProperty(x => x.AvailableAt, index == 1 ? now.AddMinutes(15) : now.AddMinutes(-31))
                .SetProperty(x => x.LeaseOwner, state == "leased" ? "test-owner" : null)
                .SetProperty(x => x.LeaseExpiresAt, state == "leased" ? (DateTimeOffset?)pgNow.AddMinutes(index == 2 ? 5 : -5) : null)
                .SetProperty(x => x.CompletedAt, state == "completed" ? (DateTimeOffset?)now.AddHours(index == 7 ? -24 : -25)
                    : state == "failed" || state == "cancelled" ? (DateTimeOffset?)now : null));
        }
        var before = await BusinessSnapshot(ws); var snapshot = await Supervise(ws);
        Assert.Equal(new AutomationQueueSummary(1,1,1,1,1,1,1,1,now.AddHours(-2),now.AddMinutes(-31),
            now.AddMinutes(15),now), snapshot.Queue);
        Assert.Contains(snapshot.Alerts, x => x.Code == AutomationAlertCodes.ExpiredLeases && x.Count == 1);
        Assert.Contains(snapshot.Alerts, x => x.Code == AutomationAlertCodes.WorkerDisabled && x.Count == 4);
        Assert.Contains(snapshot.Alerts, x => x.Code == AutomationAlertCodes.RuntimeDisabled && x.Count == 4);
        Assert.Contains(snapshot.Alerts, x => x.Code == AutomationAlertCodes.PendingBacklog && x.Count == 2);
        Assert.Equal(before, await BusinessSnapshot(ws));
    }

    [Fact]
    public async Task HumanSummaryUsesTheSameStaleHelperAsTheExistingApiIncludingCorruptSnapshots()
    {
        var first = await Prepare("manual"); await Run(first);
        var corrupt = await Prepare("assist", workspace: first.Workspace); await Run(corrupt);
        var stale = await Prepare("manual", workspace: first.Workspace); await Run(stale);
        await ChangeRule(stale.Rule.Id, r => r.ActionConfigurationJson = "{\"title\":\"Changed\"}");
        await using (var db = Db()) await db.AutomationActionRequests.Where(x => x.AutomationJobId == corrupt.Job.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.ActionPlanJson, "{\"secret-marker\":42}"));
        var approved = await Prepare("manual", workspace: first.Workspace); await Run(approved); await Decide(approved); await Run(approved);
        var rejected = await Prepare("assist", workspace: first.Workspace); await Run(rejected); await Decide(rejected, false);
        var foreign = await Prepare("manual"); await Run(foreign);
        var before = await BusinessSnapshot(first.Workspace);
        using var factory = SupervisionFactory(first.Workspace); using var client = Client(factory);
        var snapshot = (await client.GetFromJsonAsync<AutomationSupervisionDto>("/api/automation-supervision"))!;
        Assert.Equal(new AutomationRequestSummary(3,2,1,1,Clock.Now,Clock.Now), snapshot.ActionRequests);
        var request = await RequestFor(corrupt.Job.Id);
        var read = (await client.GetFromJsonAsync<AutomationActionRequestDto>("/api/automation-action-requests/" + request.Id))!;
        Assert.True(read.IsStale); Assert.Null(read.ActionPlan);
        var refused = await client.PostAsJsonAsync("/api/automation-action-requests/" + request.Id + "/approve", new { });
        await Problem(refused, HttpStatusCode.Conflict, "ActionRequestStale");
        Assert.Contains(snapshot.Alerts, x => x.Code == AutomationAlertCodes.StaleRequests && x.Count == 2);
        Assert.Equal(before, await BusinessSnapshot(first.Workspace));
    }

    [Fact]
    public async Task ExecutionCountsBoundariesAndAbandonmentMatchRecovery()
    {
        var setup = await Prepare(); await Settings(setup.Workspace, s => s.MaxConsecutiveFailures = 20);
        await Outcome(setup, 1, true, Clock.Now.AddHours(-24));
        await Outcome(setup, 2, false, Clock.Now.AddHours(-25));
        await Outcome(setup, 3, false, Clock.Now.AddHours(-1));
        SupervisionOptions.RecentFailureWindowHours = 2;
        var human = await Prepare("manual", workspace: setup.Workspace);
        // Remove the unused original job before driving the shared workspace queue.
        await using (var db = Db()) await db.AutomationJobs.Where(x => x.Id == setup.Job.Id).ExecuteDeleteAsync();
        await Run(human); await Decide(human); await Run(human);
        var skipped = await Prepare(workspace: setup.Workspace);
        await ChangeRule(skipped.Rule.Id, r => r.Enabled = false); await Run(skipped);
        var active = await Prepare(workspace: setup.Workspace); var activeClaim = await Claim(setup.Workspace);
        var expired = await Prepare(workspace: setup.Workspace); var expiredClaim = await Claim(setup.Workspace);
        var mismatch = await Prepare(workspace: setup.Workspace); var mismatchClaim = await Claim(setup.Workspace);
        await using (var db = Db())
        {
            var runtime = Runtime(db);
            foreach (var claim in new[] { activeClaim, expiredClaim, mismatchClaim })
                db.Add(runtime.NewAttempt(claim, await runtime.ReadAsync(claim, false, default)));
            await db.SaveChangesAsync();
            await db.AutomationJobs.Where(x => x.Id == mismatchClaim.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.AttemptCount, 2));
        }
        await Expire(expiredClaim.Id);
        var before = await BusinessSnapshot(setup.Workspace); var snapshot = await Supervise(setup.Workspace);
        Assert.Equal(new AutomationExecutionSummary(3,2,2,1,1,1,1,1,Clock.Now,Clock.Now), snapshot.Executions);
        Assert.Equal(before, await BusinessSnapshot(setup.Workspace));
        await Recover(setup.Workspace);
        var recovered = await Supervise(setup.Workspace);
        Assert.Equal(1, recovered.Executions.RunningCount); Assert.Equal(0, recovered.Executions.AbandonedRunningCount);
        Assert.Equal(3, recovered.Executions.RecentTechnicalFailuresCount);
    }

    [Fact]
    public async Task QuotasMatchRuntimeAtExactMinuteAndUtcMidnightAndIgnoreHumanEffects()
    {
        var setup = await Approved(); await Run(setup);
        await Settings(setup.Workspace, s => { s.OperatingModeCode = "automatic"; s.MaxExecutionsPerMinute = 1; s.MaxExecutionsPerDay = 1; });
        var automatic = setup with { Job = await Another(setup) }; await Run(automatic);
        var snapshot = await Supervise(setup.Workspace);
        Assert.Equal(1, snapshot.Quotas.Minute.Used); Assert.Equal(1, snapshot.Quotas.Day.Used);
        Assert.Equal(Clock.Now.AddMinutes(1), snapshot.Quotas.Minute.AvailableAgainAt);
        Assert.Equal(new DateTimeOffset(2030,1,3,0,0,0,TimeSpan.Zero), snapshot.Quotas.Day.AvailableAgainAt);
        await using var db = Db();
        var settings = await db.AutomationRuntimeSettings.SingleAsync(x => x.WorkspaceId == setup.Workspace);
        Assert.Equal(("daily-quota", snapshot.Quotas.Day.AvailableAgainAt!.Value), await Runtime(db).DeferredAsync(settings, default));
        await Settings(setup.Workspace, s => s.MaxExecutionsPerDay = 100);
        db.ChangeTracker.Clear(); settings = await db.AutomationRuntimeSettings.SingleAsync(x => x.WorkspaceId == setup.Workspace);
        Assert.Equal(("minute-quota", snapshot.Quotas.Minute.AvailableAgainAt!.Value), await Runtime(db).DeferredAsync(settings, default));
        Clock.Now = Clock.Now.AddMinutes(1); Assert.Equal(0, (await Supervise(setup.Workspace)).Quotas.Minute.Used);
        Assert.Null(await Runtime(db).DeferredAsync(settings, default));
        Clock.Now = new(2030,1,3,0,0,0,TimeSpan.Zero);
        Assert.Equal(0, (await Supervise(setup.Workspace)).Quotas.Day.Used);
    }

    [Theory]
    [InlineData("/api/automation-supervision")][InlineData("/api/automation-circuit-breaker")]
    [InlineData("/api/automation-circuit-breaker/resets")]
    public async Task MissingSettingsAreControlledAndNeverRecreated(string route)
    {
        var workspace = await Workspace();
        await using var db = Db(); await db.AutomationRuntimeSettings.Where(x => x.WorkspaceId == workspace).ExecuteDeleteAsync();
        using var factory = SupervisionFactory(workspace); using var client = Client(factory);
        await Problem(await client.GetAsync(route), HttpStatusCode.Conflict, "AutomationSettingsMissing");
        await Problem(await client.PostAsJsonAsync("/api/automation-circuit-breaker/reset", new { }),
            HttpStatusCode.Conflict, "AutomationSettingsMissing");
        Assert.False(await db.AutomationRuntimeSettings.AnyAsync(x => x.WorkspaceId == workspace));
    }

    [Fact]
    public async Task AmbiguousWorkspaceCannotBeSelectedByClientQuery()
    {
        await Workspace(); await Workspace();
        using var factory = SupervisionFactory(null); using var client = Client(factory);
        await Problem(await client.GetAsync("/api/automation-supervision?workspaceId=" + Guid.NewGuid()),
            HttpStatusCode.Conflict, "WorkspaceUnavailable");
        await Problem(await client.GetAsync("/api/automation-circuit-breaker"), HttpStatusCode.Conflict, "WorkspaceUnavailable");
    }
}
