using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using ProspectionCrm.Api.Dtos.AutomationJobs;
using ProspectionCrm.Api.Entities;
using ProspectionCrm.Api.Services.Automation;
using Xunit;

namespace ProspectionCrm.Api.Tests;

public sealed class AutomationActionRequestResilienceTests(AutomationJobDatabase database)
    : AutomationActionRequestFixture(database), IClassFixture<AutomationJobDatabase>
{
    private static IAutomationActionExecutor Failure(bool transient) => new DelegateExecutor((_, _, _, _) =>
        { if (transient) throw new NpgsqlException("secret-marker", new TimeoutException()); throw new InvalidOperationException("secret-marker"); });

    [Theory]
    [InlineData(true)][InlineData(false)]
    public async Task HumanFailuresKeepApprovalAndUseTheExistingRetryBudget(bool transient)
    {
        var setup = await Approved(); var request = await RequestFor(setup.Job.Id);
        for (var i = 1; i <= (transient ? 3 : 1); i++)
        {
            await Run(setup, (_, _) => Failure(transient)); var job = await Job(setup.Job.Id);
            Assert.Equal(transient && i < 3 ? "pending" : "failed", job.StatusCode);
            if (transient && i < 3) { Assert.Equal(Clock.Now.AddSeconds(i == 1 ? 30 : 120), job.AvailableAt); await Available(job.Id); }
        }
        var executions = await Executions(setup.Job.Id); Assert.Equal(transient ? 3 : 1, executions.Count);
        Assert.All(executions, x => { Assert.True(x.IsHumanApprovedAttempt); Assert.False(x.IsAutomaticAttempt);
            Assert.Equal(request.Id, x.AutomationActionRequestId); Assert.Null(x.OutcomeSequence); Assert.False(x.EffectApplied);
            Assert.DoesNotContain("secret-marker", x.ContextJson); Assert.Equal("failed", x.StatusCode); });
        Assert.Equal("approved", (await RequestFor(setup.Job.Id)).StatusCode); Assert.Empty(await Tasks(setup.Job.Id));
    }

    [Fact]
    public async Task HumanTransientFailureThenSuccessUsesTheSameSnapshot()
    {
        var setup = await Approved(); await Run(setup, (_, _) => Failure(true));
        await Available(setup.Job.Id); await Run(setup);
        Assert.Equal(2, (await Executions(setup.Job.Id)).Count); Assert.Single(await Tasks(setup.Job.Id));
        Assert.All(await Executions(setup.Job.Id), x => Assert.Null(x.OutcomeSequence));
    }

    [Fact]
    public async Task HumanEffectsBypassAndDoNotConsumeAutomaticQuotas()
    {
        var setup = await Approved();
        await Settings(setup.Workspace, s => { s.OperatingModeCode = "automatic"; s.MaxExecutionsPerMinute = 1; s.MaxExecutionsPerDay = 1; });
        await Run(setup);
        var auto = await Another(setup); await Process(await Claim(setup.Workspace)); Assert.Single(await Tasks(auto.Id));
        await Settings(setup.Workspace, s => s.OperatingModeCode = "manual");
        var second = await Another(setup); var human = setup with { Job = second }; await Run(human); await Decide(human); await Run(human);
        Assert.Single(await Tasks(second.Id));
        await Settings(setup.Workspace, s => s.OperatingModeCode = "automatic");
        var blocked = await Another(setup); await Process(await Claim(setup.Workspace));
        Assert.Empty(await Tasks(blocked.Id)); Assert.Empty(await Executions(blocked.Id)); Assert.Equal("pending", (await Job(blocked.Id)).StatusCode);
    }

    [Fact]
    public async Task HumanSuccessBypassesOpenCircuitWithoutResettingItAndHumanFailureDoesNotOpenIt()
    {
        var setup = await Approved(); await Settings(setup.Workspace, s => { s.OperatingModeCode = "automatic"; s.MaxConsecutiveFailures = 1; });
        await Run(setup, (_, _) => Failure(false));
        var auto = await Another(setup); await Process(await Claim(setup.Workspace)); Assert.Single(await Tasks(auto.Id));
        var failed = await Another(setup); await Process(await Claim(setup.Workspace), (_, _) => Failure(false));
        Assert.Equal("failed", (await Job(failed.Id)).StatusCode);
        await Settings(setup.Workspace, s => s.OperatingModeCode = "manual");
        var second = await Another(setup); var human = setup with { Job = second }; await Run(human); await Decide(human); await Run(human);
        Assert.Single(await Tasks(second.Id));
        await Settings(setup.Workspace, s => s.OperatingModeCode = "automatic");
        var blocked = await Another(setup); await Process(await Claim(setup.Workspace)); Assert.Empty(await Executions(blocked.Id));
        Assert.Equal("pending", (await Job(blocked.Id)).StatusCode);
    }

    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task CrashAfterHumanEffectDoesNotDuplicateEvenWhenTaskWasDeleted(bool delete)
    {
        var setup = await Approved(); var claim = await Claim(setup.Workspace);
        await Process(claim, queue: q => new BrokenCompletion(q));
        Assert.Equal("leased", (await Job(claim.Id)).StatusCode); Assert.Single(await Tasks(claim.Id));
        if (delete) { await using var db = Db(); await db.CrmTasks.Where(x => x.AutomationJobId == claim.Id).ExecuteDeleteAsync(); }
        await Settings(setup.Workspace, s => s.IsEnabled = false);
        await Expire(claim.Id); await Recover(setup.Workspace); await Run(setup);
        Assert.Equal(delete ? 0 : 1, (await Tasks(claim.Id)).Count); Assert.Single(await Executions(claim.Id));
        Assert.Equal("completed", (await Job(claim.Id)).StatusCode); Assert.Equal("approved", (await RequestFor(claim.Id)).StatusCode);
    }

    [Fact]
    public async Task ExistingTaskWithoutHistoryIsAdoptedAsHuman()
    {
        var setup = await Approved();
        await using (var db = Db()) { db.Add(new CrmTask { Title = "Existing", OpportunityId = setup.Opportunity.Id, AutomationJobId = setup.Job.Id }); await db.SaveChangesAsync(); }
        await Run(setup); var execution = Assert.Single(await Executions(setup.Job.Id));
        Assert.Equal("effect-already-applied", execution.ReasonCode); Assert.True(execution.IsHumanApprovedAttempt);
        Assert.Null(execution.OutcomeSequence); Assert.Single(await Tasks(setup.Job.Id));
    }

    private sealed class Crash(CancellationTokenSource stop, bool after) : DbTransactionInterceptor
    {
        private void Fail() { stop.Cancel(); stop.Token.ThrowIfCancellationRequested(); }
        public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction,
            TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        { if (!after) Fail(); return ValueTask.FromResult(result); }
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData,
            CancellationToken cancellationToken = default) { if (after) Fail(); return Task.CompletedTask; }
    }

    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task RequestCommitIsAtomicUnderCrash(bool after)
    {
        var setup = await Prepare("manual"); var claim = await Claim(setup.Workspace); using var stop = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Process(claim, token: stop.Token, interceptor: new Crash(stop, after)));
        Assert.Equal(after ? "awaiting-approval" : "leased", (await Job(claim.Id)).StatusCode);
        await using var db = Db(); Assert.Equal(after ? 1 : 0, await db.AutomationActionRequests.CountAsync(x => x.AutomationJobId == claim.Id));
        Assert.Empty(await Executions(claim.Id)); Assert.Empty(await Tasks(claim.Id));
        if (!after) { await Expire(claim.Id); await Recover(setup.Workspace); await Run(setup); }
        Assert.Equal("awaiting-approval", (await Job(claim.Id)).StatusCode);
    }

    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task DecisionCommitIsAtomicUnderCrash(bool after)
    {
        var setup = await Prepare("manual"); await Run(setup); using var stop = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Decide(setup, interceptor: new Crash(stop, after), token: stop.Token));
        Assert.Equal(after ? "approved" : "pending", (await RequestFor(setup.Job.Id)).StatusCode);
        Assert.Equal(after ? "pending" : "awaiting-approval", (await Job(setup.Job.Id)).StatusCode);
        Assert.Empty(await Executions(setup.Job.Id)); Assert.Empty(await Tasks(setup.Job.Id));
    }

    private sealed class BrokenCompletion(IAutomationJobQueue inner) : IAutomationJobQueue
    {
        public Task<bool> CompleteAsync(Guid w, Guid j, string o, CancellationToken t) => throw new NpgsqlException("secret-marker");
        public Task<AutomationJobResult<AutomationJob>> EnqueueAsync(Guid w, EnqueueAutomationJobRequest r, CancellationToken t) => inner.EnqueueAsync(w, r, t);
        public Task<AutomationJob?> ClaimAsync(Guid w, CancellationToken t) => inner.ClaimAsync(w, t);
        public Task<bool> FailAsync(Guid w, Guid j, string o, string? e, CancellationToken t) => inner.FailAsync(w, j, o, e, t);
        public Task<bool> ReleaseAsync(Guid w, Guid j, string o, DateTimeOffset? a, CancellationToken t) => inner.ReleaseAsync(w, j, o, a, t);
        public Task<bool> CancelAsync(Guid w, Guid j, CancellationToken t) => inner.CancelAsync(w, j, t);
        public Task<int> RecoverAsync(Guid w, CancellationToken t) => inner.RecoverAsync(w, t);
    }
}
