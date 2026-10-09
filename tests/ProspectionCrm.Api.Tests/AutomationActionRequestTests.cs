using Microsoft.EntityFrameworkCore;
using Npgsql;
using ProspectionCrm.Api.Entities;
using ProspectionCrm.Api.Services.Automation;
using Xunit;

namespace ProspectionCrm.Api.Tests;

public sealed class AutomationActionRequestTests(AutomationJobDatabase database)
    : AutomationActionRequestFixture(database), IClassFixture<AutomationJobDatabase>
{
    [Theory]
    [InlineData("manual", "manual")][InlineData("assist", "approval-required")]
    public async Task HumanModesPersistOneRequestAndNoExecutionOrTask(string mode, string requirement)
    {
        var setup = await Prepare(mode); var claim = await Claim(setup.Workspace);
        await Task.WhenAll(Process(claim), Process(claim)); await Process(claim);
        var request = await RequestFor(claim.Id); var job = await Job(claim.Id);
        Assert.Equal(requirement, request.DecisionRequirementCode); Assert.Equal("pending", request.StatusCode);
        Assert.Equal(Clock.Now, request.RequestedAt); Assert.Null(request.DecidedAt); Assert.Null(request.DecidedByUserId);
        Assert.Equal("awaiting-approval", job.StatusCode); Assert.Null(job.LeaseOwner); Assert.Null(job.LeaseExpiresAt);
        Assert.Null(job.CompletedAt); Assert.Null(job.LastError); Assert.Equal(1, job.AttemptCount);
        Assert.Equal(claim.AvailableAt, job.AvailableAt);
        Assert.Empty(await Executions(claim.Id)); Assert.Empty(await Tasks(claim.Id));
        await using var db = Db(); Assert.Null(await Queue(db).ClaimAsync(setup.Workspace, default));
        Assert.Equal(0, await Recover(setup.Workspace)); Assert.False(await Queue(db).CancelAsync(setup.Workspace, claim.Id, default));
        Assert.False(await Queue(db).CompleteAsync(setup.Workspace, claim.Id, claim.LeaseOwner!, default));
        Assert.False(await Queue(db).FailAsync(setup.Workspace, claim.Id, claim.LeaseOwner!, null, default));
    }

    [Theory]
    [InlineData(true)][InlineData(false)]
    public async Task DecisionsAreIdempotentAndOppositeDecisionConflicts(bool approve)
    {
        var setup = await Prepare("manual"); await Run(setup);
        var first = await Decide(setup, approve, "First note"); Clock.Now = Clock.Now.AddHours(1);
        var again = await Decide(setup, approve, "Replacement");
        Assert.Equal(200, first.Status); Assert.Equal(200, again.Status);
        Assert.Equal(first.Value!.DecidedAt, again.Value!.DecidedAt); Assert.Equal("First note", again.Value.DecisionNote);
        Assert.Equal(409, (await Decide(setup, !approve)).Status);
        Assert.Equal(approve ? "pending" : "cancelled", (await Job(setup.Job.Id)).StatusCode);
        Assert.Empty(await Executions(setup.Job.Id)); Assert.Empty(await Tasks(setup.Job.Id));
        await using var db = Db();
        Assert.Equal((await db.Workspaces.SingleAsync(x => x.Id == setup.Workspace)).OwnerUserId, first.Value.DecidedByUserId);
    }

    [Fact]
    public async Task OppositeConcurrentDecisionsHaveOneWinner()
    {
        var setup = await Prepare("assist"); await Run(setup);
        var results = await Task.WhenAll(Decide(setup, true), Decide(setup, false));
        Assert.Single(results, x => x.Status == 200); Assert.Single(results, x => x.Status == 409);
        var request = await RequestFor(setup.Job.Id);
        Assert.Equal(request.StatusCode == "approved" ? "pending" : "cancelled", (await Job(setup.Job.Id)).StatusCode);
        Assert.Empty(await Tasks(setup.Job.Id)); Assert.Empty(await Executions(setup.Job.Id));
    }

    [Theory]
    [InlineData("manual")][InlineData("assist")][InlineData("automatic")]
    public async Task GlobalModeChangeDoesNotDecideAndApprovedSnapshotExecutesOnce(string mode)
    {
        var setup = await Prepare("manual"); await Run(setup); var before = await RequestFor(setup.Job.Id);
        await Settings(setup.Workspace, s => s.OperatingModeCode = mode);
        await using (var db = Db()) Assert.Null(await Queue(db).ClaimAsync(setup.Workspace, default));
        Assert.Equal("pending", (await RequestFor(setup.Job.Id)).StatusCode);
        Assert.Equal(200, (await Decide(setup)).Status); Assert.Empty(await Tasks(setup.Job.Id));
        await Run(setup);
        var execution = Assert.Single(await Executions(setup.Job.Id));
        Assert.True(execution.IsHumanApprovedAttempt); Assert.False(execution.IsAutomaticAttempt);
        Assert.Equal(before.Id, execution.AutomationActionRequestId); Assert.True(execution.EffectApplied); Assert.Null(execution.OutcomeSequence);
        Assert.Equal("Relancer", Assert.Single(await Tasks(setup.Job.Id)).Title);
        Assert.Equal(before.ActionPlanJson, (await RequestFor(setup.Job.Id)).ActionPlanJson);
        Assert.Equal(200, (await Decide(setup)).Status); Assert.Equal("completed", (await Job(setup.Job.Id)).StatusCode);
    }

    [Fact]
    public async Task KillSwitchPreventsPreparationAndDefersApprovedEffectsWithoutNewHistory()
    {
        var setup = await Prepare("manual", enabled: false); await Run(setup);
        await using (var db = Db()) Assert.Empty(await db.AutomationActionRequests.Where(x => x.AutomationJobId == setup.Job.Id).ToListAsync());
        await Settings(setup.Workspace, s => s.IsEnabled = true); await Available(setup.Job.Id); await Run(setup);
        await Settings(setup.Workspace, s => s.IsEnabled = false);
        Assert.Equal(200, (await Decide(setup)).Status);
        for (var i = 0; i < 4; i++) { await Available(setup.Job.Id); await Run(setup); }
        Assert.Empty(await Tasks(setup.Job.Id)); Assert.Empty(await Executions(setup.Job.Id));
        Assert.Equal("approved", (await RequestFor(setup.Job.Id)).StatusCode);
        await Settings(setup.Workspace, s => s.IsEnabled = true); await Available(setup.Job.Id); await Run(setup);
        Assert.Single(await Tasks(setup.Job.Id));
    }

    [Theory]
    [InlineData("stale")][InlineData("disabled")][InlineData("archived")]
    public async Task UnavailableRulesCannotBeApprovedButCanBeRejected(string change)
    {
        var setup = await Prepare("manual"); await Run(setup);
        await ChangeRule(setup.Rule.Id, r => { if (change == "stale") r.ActionConfigurationJson = """{"title":"Changed"}""";
            else if (change == "disabled") r.Enabled = false; else r.ArchivedAt = Clock.Now; });
        var result = await Decide(setup); Assert.Equal(409, result.Status);
        Assert.Equal(change == "stale" ? "ActionRequestStale" : "ActionRequestUnavailable", result.Code);
        Assert.Equal("pending", (await RequestFor(setup.Job.Id)).StatusCode);
        Assert.Equal("awaiting-approval", (await Job(setup.Job.Id)).StatusCode);
        Assert.Equal(200, (await Decide(setup, false)).Status); Assert.Empty(await Executions(setup.Job.Id));
    }

    [Theory]
    [InlineData("stale", "action-request-stale")][InlineData("disabled", "rule-disabled")]
    [InlineData("archived", "rule-archived")][InlineData("plan", "action-request-invalid-plan")]
    [InlineData("opportunity", "opportunity-archived")][InlineData("missing", "opportunity-not-found")]
    public async Task ApprovedBusinessFailuresAreTerminalHumanSkips(string change, string reason)
    {
        var setup = await Approved();
        if (change is "stale" or "disabled" or "archived")
            await ChangeRule(setup.Rule.Id, r => { if (change == "stale") r.ActionConfigurationJson = """{"title":"Changed"}""";
                else if (change == "disabled") r.Enabled = false; else r.ArchivedAt = Clock.Now; });
        else
        {
            await using var db = Db();
            if (change == "plan") await db.AutomationActionRequests.Where(x => x.AutomationJobId == setup.Job.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.ActionPlanJson, "{}"));
            else if (change == "missing") await db.Opportunities.Where(x => x.Id == setup.Opportunity.Id).ExecuteDeleteAsync();
            else await db.Opportunities.Where(x => x.Id == setup.Opportunity.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.ArchivedAt, Clock.Now));
        }
        await Run(setup); var execution = Assert.Single(await Executions(setup.Job.Id));
        Assert.True(execution.IsHumanApprovedAttempt); Assert.False(execution.IsAutomaticAttempt);
        Assert.Equal("skipped", execution.StatusCode); Assert.Equal(reason, execution.ReasonCode);
        Assert.Null(execution.OutcomeSequence); Assert.False(execution.EffectApplied); Assert.Empty(await Tasks(setup.Job.Id));
        Assert.Equal("completed", (await Job(setup.Job.Id)).StatusCode); Assert.Equal("approved", (await RequestFor(setup.Job.Id)).StatusCode);
    }

    [Fact]
    public async Task UniqueAndWorkspaceConstraintsProtectDirectWriters()
    {
        var setup = await Prepare("manual"); await Run(setup); var request = await RequestFor(setup.Job.Id);
        await using var db = Db(); request.Id = Guid.NewGuid(); db.Add(request);
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Equal("23505", Assert.IsType<PostgresException>(error.InnerException).SqlState); db.ChangeTracker.Clear();
        var other = await Prepare("manual"); request.AutomationJobId = other.Job.Id;
        db.Add(request); error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Equal("23503", Assert.IsType<PostgresException>(error.InnerException).SqlState);
    }

    [Fact]
    public async Task ExpiredOrForeignOwnerCannotCreateARequest()
    {
        var setup = await Prepare("manual"); var claim = await Claim(setup.Workspace);
        await Process(claim, owner: "foreign"); await Expire(claim.Id); await Process(claim);
        await using var db = Db(); Assert.Empty(await db.AutomationActionRequests.Where(x => x.AutomationJobId == claim.Id).ToListAsync());
        Assert.Empty(await Executions(claim.Id)); await Recover(setup.Workspace); await Run(setup);
        Assert.Equal("pending", (await RequestFor(claim.Id)).StatusCode);
    }

    [Fact]
    public async Task CancelledRequestRejectsBothDecisionsWithoutRequeue()
    {
        var setup = await Prepare("manual"); await Run(setup);
        await using (var db = Db()) await db.AutomationActionRequests.Where(x => x.AutomationJobId == setup.Job.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.StatusCode, "cancelled").SetProperty(x => x.DecidedAt, Clock.Now));
        Assert.Equal(409, (await Decide(setup)).Status); Assert.Equal(409, (await Decide(setup, false)).Status);
        Assert.Empty(await Tasks(setup.Job.Id)); Assert.Empty(await Executions(setup.Job.Id));
    }
}
