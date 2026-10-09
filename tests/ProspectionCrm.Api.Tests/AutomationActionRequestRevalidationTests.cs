using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using ProspectionCrm.Api.Services.Automation;
using Xunit;

namespace ProspectionCrm.Api.Tests;

public sealed class AutomationActionRequestRevalidationTests(AutomationJobDatabase database)
    : AutomationActionRequestFixture(database), IClassFixture<AutomationJobDatabase>
{
    private sealed class AfterStart(Func<Task> change) : DbTransactionInterceptor
    {
        private int count;
        public override async Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData,
            CancellationToken cancellationToken = default) { if (Interlocked.Increment(ref count) == 1) await change(); }
    }

    [Fact]
    public async Task RuleChangedAfterRunningCheckpointCannotAuthorizeTheSnapshot()
    {
        var setup = await Approved();
        await Process(await Claim(setup.Workspace), interceptor: new AfterStart(() =>
            ChangeRule(setup.Rule.Id, r => r.ActionConfigurationJson = """{"title":"Changed"}""")));
        Assert.Empty(await Tasks(setup.Job.Id)); Assert.Equal("action-request-stale", Assert.Single(await Executions(setup.Job.Id)).ReasonCode);
    }

    [Fact]
    public async Task LateKillSwitchDefersHumanAttemptAndResumesWithoutLosingApproval()
    {
        var setup = await Approved();
        await Process(await Claim(setup.Workspace), interceptor: new AfterStart(() => Settings(setup.Workspace, s => s.IsEnabled = false)));
        var deferred = Assert.Single(await Executions(setup.Job.Id)); Assert.True(deferred.IsDeferred); Assert.True(deferred.IsHumanApprovedAttempt);
        Assert.Equal("pending", (await Job(setup.Job.Id)).StatusCode); Assert.Empty(await Tasks(setup.Job.Id));
        await Settings(setup.Workspace, s => s.IsEnabled = true); await Available(setup.Job.Id); await Run(setup);
        Assert.Single(await Tasks(setup.Job.Id)); Assert.Equal(2, (await Executions(setup.Job.Id)).Count);
    }

    [Fact]
    public async Task LeaseExpiryDuringHumanMutationRollsBackEffectAndRecoveryKeepsHumanOrigin()
    {
        var setup = await Approved(); var claim = await Claim(setup.Workspace);
        await Process(claim, (db, runtime) => new DelegateExecutor(async (job, plan, now, token) =>
        {
            var result = await new CreateCrmTaskAutomationExecutor(db, runtime).ExecuteAsync(job, plan, now, token);
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE "AutomationJobs" SET "LeaseExpiresAt" = clock_timestamp() - interval '1 second' WHERE "Id" = {job.Id}
                """, token); return result;
        }));
        Assert.Empty(await Tasks(claim.Id)); Assert.Equal("running", Assert.Single(await Executions(claim.Id)).StatusCode);
        await Expire(claim.Id); await Recover(setup.Workspace); await Run(setup);
        var rows = await Executions(claim.Id); Assert.Equal(2, rows.Count); Assert.Equal("lease-expired", rows[0].ReasonCode);
        Assert.All(rows, x => { Assert.True(x.IsHumanApprovedAttempt); Assert.Null(x.OutcomeSequence); });
        Assert.Single(await Tasks(claim.Id));
    }

    [Fact]
    public async Task QueueContextChangesCannotReconstructAnApprovedPlan()
    {
        var setup = await Approved();
        await using (var db = Db()) await db.AutomationJobs.Where(x => x.Id == setup.Job.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.ContextJson, "{}"));
        await Run(setup); Assert.Equal(setup.Opportunity.Id, Assert.Single(await Tasks(setup.Job.Id)).OpportunityId);
    }

    [Fact]
    public async Task ConcurrentApprovedProcessorsOnlyApplyOneEffect()
    {
        var setup = await Approved(); var claim = await Claim(setup.Workspace);
        await Task.WhenAll(Process(claim), Process(claim)); await Process(claim);
        Assert.Single(await Tasks(claim.Id)); Assert.Single(await Executions(claim.Id));
    }
}
