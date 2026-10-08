using Microsoft.EntityFrameworkCore;
using Npgsql;
using ProspectionCrm.Api.Dtos.AutomationJobs;
using ProspectionCrm.Api.Entities;
using ProspectionCrm.Api.Services.Automation;
using Xunit;

namespace ProspectionCrm.Api.Tests;

public sealed class AutomationRuntimeResilienceTests(AutomationJobDatabase database)
    : AutomationRuntimeFixture(database), IClassFixture<AutomationJobDatabase>
{
    private static IAutomationActionExecutor Failure(bool transient = true) => new DelegateExecutor((_, _, _, _) =>
        { if (transient) throw new NpgsqlException("secret-marker", new TimeoutException("secret-marker"));
            throw new InvalidOperationException("secret-marker"); });

    private sealed class FailingEvaluator(bool transient) : IAutomationRuleEvaluator
    {
        public AutomationEvaluationResult Evaluate(AutomationJob job, AutomationRule rule, AutomationRuntimeSettings settings)
        {
            if (transient) throw new NpgsqlException("secret-marker", new TimeoutException("secret-marker"));
            throw new InvalidOperationException("secret-marker");
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailureBeforeRunningHistoryIsAlsoDiagnosedAndBounded(bool transient)
    {
        var setup = await Prepare();
        for (var i = 1; i <= (transient ? 3 : 1); i++)
        {
            await Process(await Claim(setup.Workspace), evaluator: new FailingEvaluator(transient));
            var job = await Job(setup.Job.Id);
            var retry = transient && i < 3;
            Assert.Equal(retry ? "pending" : "failed", job.StatusCode);
            if (retry)
            {
                Assert.Equal(Clock.Now.AddSeconds(i == 1 ? 30 : 120), job.AvailableAt);
                await Available(job.Id);
            }
        }
        var history = await Executions(setup.Job.Id);
        Assert.Equal(transient ? 3 : 1, history.Count);
        Assert.All(history, x => { Assert.Equal("failed", x.StatusCode); Assert.NotNull(x.FinishedAt);
            Assert.Null(x.OutcomeSequence); Assert.DoesNotContain("secret-marker", x.ContextJson); });
        Assert.Empty(await Tasks(setup.Job.Id));
    }

    [Fact]
    public async Task EarlyFailureWhileKillSwitchIsOffPreservesTheEventWithoutConsumingRetries()
    {
        var setup = await Prepare(enabled: false);
        await Process(await Claim(setup.Workspace), evaluator: new FailingEvaluator(false));
        Assert.Equal("pending", (await Job(setup.Job.Id)).StatusCode);
        Assert.Empty(await Executions(setup.Job.Id)); Assert.Empty(await Tasks(setup.Job.Id));
    }

    [Fact]
    public async Task EvaluationFailureDuringReplayCannotRewriteCommittedSuccess()
    {
        var setup = await Prepare(); var claim = await Claim(setup.Workspace);
        await Process(claim, queue: q => new BrokenCompletion(q));
        await Expire(claim.Id); await Recover(setup.Workspace);
        await Process(await Claim(setup.Workspace), evaluator: new FailingEvaluator(false));
        Assert.Equal("completed", (await Job(claim.Id)).StatusCode);
        Assert.Single(await Tasks(claim.Id)); Assert.True(Assert.Single(await Executions(claim.Id)).EffectApplied);
    }

    [Fact]
    public async Task TransientFailuresUseSeparateThreeAttemptBudgetAndDeterministicBackoff()
    {
        var setup = await Prepare();
        for (var i = 1; i <= 3; i++)
        {
            await Run(setup, (_, _) => Failure());
            var job = await Job(setup.Job.Id);
            Assert.Equal(i, job.AttemptCount); Assert.Equal(i == 3 ? "failed" : "pending", job.StatusCode);
            if (i < 3) { Assert.Equal(Clock.Now.AddSeconds(i == 1 ? 30 : 120), job.AvailableAt); await Available(job.Id); }
        }
        var executions = await Executions(setup.Job.Id);
        Assert.Equal(3, executions.Count);
        Assert.All(executions, e => { Assert.Equal("failed", e.StatusCode); Assert.Equal("transient-failure", e.ErrorMessage);
            Assert.NotNull(e.FinishedAt); Assert.False(e.EffectApplied); Assert.DoesNotContain("secret-marker", e.ContextJson); });
        Assert.Null(executions[0].OutcomeSequence); Assert.Null(executions[1].OutcomeSequence);
        Assert.NotNull(executions[2].OutcomeSequence); Assert.Empty(await Tasks(setup.Job.Id));
        Assert.Equal(AutomationJobErrors.JobFailed, (await Job(setup.Job.Id)).LastError);
    }

    [Fact]
    public async Task UnknownTechnicalFailureIsTerminalAndSanitized()
    {
        var setup = await Prepare(); await Run(setup, (_, _) => Failure(false));
        Assert.Equal("failed", (await Job(setup.Job.Id)).StatusCode);
        var failure = Assert.Single(await Executions(setup.Job.Id));
        Assert.Equal("technical-failure", failure.ErrorMessage); Assert.NotNull(failure.OutcomeSequence);
    }

    [Fact]
    public async Task RetriedSuccessCountsOnlyOneAppliedEffect()
    {
        var setup = await Prepare(); await Run(setup, (_, _) => Failure());
        await Available(setup.Job.Id); await Run(setup);
        var histories = await Executions(setup.Job.Id);
        Assert.Equal(2, histories.Count); Assert.Single(histories, x => x.EffectApplied);
        Assert.Single(histories, x => x.OutcomeSequence != null); Assert.Single(await Tasks(setup.Job.Id));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CrashAfterEffectCommitBeforeCompletionIsIdempotentEvenAfterTaskDeletion(bool deleteTask)
    {
        var setup = await Prepare(); var claim = await Claim(setup.Workspace);
        await Process(claim, queue: q => new BrokenCompletion(q));
        Assert.Equal("leased", (await Job(claim.Id)).StatusCode);
        Assert.Single(await Tasks(claim.Id)); Assert.True(Assert.Single(await Executions(claim.Id)).EffectApplied);
        if (deleteTask)
        {
            await using var db = Db(); await db.CrmTasks.Where(x => x.AutomationJobId == claim.Id).ExecuteDeleteAsync();
        }
        // Reconciliation is not a new authorization or effect, even if the kill switch has changed.
        await Settings(setup.Workspace, s => s.IsEnabled = false);
        await Expire(claim.Id); await Recover(setup.Workspace);
        await Process(await Claim(setup.Workspace));
        Assert.Equal("completed", (await Job(claim.Id)).StatusCode);
        Assert.Equal(deleteTask ? 0 : 1, (await Tasks(claim.Id)).Count);
        Assert.Single(await Executions(claim.Id));
    }

    [Fact]
    public async Task PersistedTaskWithoutHistoryIsAdoptedWithoutDuplication()
    {
        var setup = await Prepare();
        await using (var db = Db())
        {
            db.Add(new CrmTask { Title = "Already created", OpportunityId = setup.Opportunity.Id,
                AutomationJobId = setup.Job.Id, CreatedAt = Clock.Now.AddMinutes(-2) });
            await db.SaveChangesAsync();
        }
        await Run(setup);
        Assert.Equal("Already created", Assert.Single(await Tasks(setup.Job.Id)).Title);
        Assert.Equal("effect-already-applied", Assert.Single(await Executions(setup.Job.Id)).ReasonCode);
        Assert.Equal("completed", (await Job(setup.Job.Id)).StatusCode);
    }

    [Fact]
    public async Task RepeatedAbandonmentIsBoundedAndBecomesOneTerminalOutcome()
    {
        var setup = await Prepare(); Config.MaxAttempts = 2;
        for (var i = 0; i < 2; i++)
        {
            var claim = await Claim(setup.Workspace);
            using var stop = new CancellationTokenSource();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Process(claim, (_, _) =>
                new DelegateExecutor((_, _, _, token) => { stop.Cancel(); token.ThrowIfCancellationRequested(); throw new InvalidOperationException(); }),
                token: stop.Token));
            await Expire(claim.Id); await Recover(setup.Workspace);
        }
        await Run(setup);
        Assert.Equal("failed", (await Job(setup.Job.Id)).StatusCode);
        var histories = await Executions(setup.Job.Id);
        Assert.Equal(2, histories.Count); Assert.All(histories, x => Assert.Equal("failed", x.StatusCode));
        Assert.Single(histories, x => x.OutcomeSequence != null); Assert.Empty(await Tasks(setup.Job.Id));
    }

    [Fact]
    public async Task SlidingMinuteQuotaDefersWithoutHistoriesAndResumesAtExactBoundary()
    {
        var setup = await Prepare(); await Settings(setup.Workspace, s => s.MaxExecutionsPerMinute = 1);
        await Run(setup); var next = await Another(setup);
        await Process(await Claim(setup.Workspace));
        Assert.Equal(Clock.Now.AddMinutes(1), (await Job(next.Id)).AvailableAt);
        Assert.Empty(await Executions(next.Id)); Assert.Empty(await Tasks(next.Id));
        Clock.Now = Clock.Now.AddSeconds(59); await Available(next.Id); await Process(await Claim(setup.Workspace));
        Assert.Empty(await Executions(next.Id));
        Clock.Now = Clock.Now.AddSeconds(1); await Available(next.Id); await Process(await Claim(setup.Workspace));
        Assert.True(Assert.Single(await Executions(next.Id)).EffectApplied); Assert.Single(await Tasks(next.Id));
    }

    [Fact]
    public async Task DailyQuotaUsesUtcMidnightAndSurvivesTaskDeletion()
    {
        Clock.Now = new(2030, 1, 2, 23, 58, 50, TimeSpan.Zero);
        var setup = await Prepare(); await Settings(setup.Workspace, s => { s.MaxExecutionsPerMinute = 1; s.MaxExecutionsPerDay = 1; });
        await Run(setup);
        await using (var db = Db()) await db.CrmTasks.Where(x => x.AutomationJobId == setup.Job.Id).ExecuteDeleteAsync();
        var next = await Another(setup); await Process(await Claim(setup.Workspace));
        var midnight = new DateTimeOffset(2030, 1, 3, 0, 0, 0, TimeSpan.Zero);
        Assert.Equal(midnight, (await Job(next.Id)).AvailableAt); Assert.Empty(await Executions(next.Id));
        Clock.Now = midnight; await Available(next.Id); await Process(await Claim(setup.Workspace));
        Assert.Single(await Tasks(next.Id));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentJobsCannotOverrunMinuteOrDayQuota(bool daily)
    {
        var setup = await Prepare();
        await Settings(setup.Workspace, s => { s.MaxExecutionsPerMinute = 1; s.MaxExecutionsPerDay = daily ? 1 : 100; });
        var next = await Another(setup); var firstClaim = await Claim(setup.Workspace); var secondClaim = await Claim(setup.Workspace);
        await Task.WhenAll(Process(firstClaim), Process(secondClaim)).WaitAsync(TimeSpan.FromSeconds(20));
        await using var db = Db();
        Assert.Equal(1, await db.CrmTasks.CountAsync(x => x.Opportunity.WorkspaceId == setup.Workspace));
        Assert.Equal(1, await db.AutomationExecutions.CountAsync(x => x.WorkspaceId == setup.Workspace && x.EffectApplied));
        var pending = Assert.Single(await db.AutomationJobs.Where(x => x.WorkspaceId == setup.Workspace && x.StatusCode == "pending").ToListAsync());
        Clock.Now = daily ? new DateTimeOffset(2030, 1, 3, 0, 0, 0, TimeSpan.Zero) : Clock.Now.AddMinutes(1);
        await Available(pending.Id); await Process(await Claim(setup.Workspace));
        Assert.Equal(2, await db.AutomationExecutions.CountAsync(x => x.WorkspaceId == setup.Workspace && x.EffectApplied));
    }

    [Fact]
    public async Task SkippedAndTransientFailuresDoNotConsumeQuotaOrTripCircuit()
    {
        var setup = await Prepare("manual");
        await Settings(setup.Workspace, s => { s.MaxExecutionsPerMinute = 1; s.MaxExecutionsPerDay = 1; s.MaxConsecutiveFailures = 1; });
        await Run(setup);
        await Settings(setup.Workspace, s => s.OperatingModeCode = "automatic");
        var next = await Another(setup); await Process(await Claim(setup.Workspace), (_, _) => Failure());
        await Available(next.Id); await Process(await Claim(setup.Workspace));
        Assert.Single(await Tasks(next.Id));
    }

    [Fact]
    public async Task CircuitCountsOnlyConsecutiveTerminalAutomaticFailuresAndIsWorkspaceScoped()
    {
        var setup = await Prepare();
        await Settings(setup.Workspace, s => s.MaxConsecutiveFailures = 2);
        await Run(setup, (_, _) => Failure(false));
        // One success resets the sequence, including when every outcome has the same timestamp.
        var success = await Another(setup); await Process(await Claim(setup.Workspace));
        var failed = await Another(setup); await Process(await Claim(setup.Workspace), (_, _) => Failure(false));
        await Settings(setup.Workspace, s => s.OperatingModeCode = "manual");
        var skipped = await Another(setup); await Process(await Claim(setup.Workspace));
        await Settings(setup.Workspace, s => s.OperatingModeCode = "automatic");
        var failedAgain = await Another(setup); await Process(await Claim(setup.Workspace), (_, _) => Failure(false));
        var blocked = await Another(setup); await Process(await Claim(setup.Workspace));
        Assert.Empty(await Executions(blocked.Id)); Assert.Empty(await Tasks(blocked.Id));
        Assert.Equal("pending", (await Job(blocked.Id)).StatusCode);
        Assert.Equal(Clock.Now.AddSeconds(60), (await Job(blocked.Id)).AvailableAt);
        var other = await Prepare(); await Run(other); Assert.Single(await Tasks(other.Job.Id));
        await using var db = Db();
        Assert.True((await db.AutomationRuntimeSettings.SingleAsync(x => x.WorkspaceId == setup.Workspace)).IsEnabled);
        Assert.Equal(4, await db.AutomationExecutions.CountAsync(x => x.WorkspaceId == setup.Workspace && x.OutcomeSequence != null));
        // Explicitly raising the threshold permits a new attempt; the engine never toggles the kill switch.
        await Settings(setup.Workspace, s => s.MaxConsecutiveFailures = 3);
        await Available(blocked.Id); await Process(await Claim(setup.Workspace));
        Assert.Single(await Tasks(blocked.Id));
    }

    [Fact]
    public async Task ConcurrentFailureAndNewActionRespectCircuitLock()
    {
        var setup = await Prepare(); await Settings(setup.Workspace, s => s.MaxConsecutiveFailures = 1);
        await Run(setup, (_, _) => Failure(false));
        var a = await Another(setup); var b = await Another(setup);
        var ca = await Claim(setup.Workspace); var cb = await Claim(setup.Workspace);
        await Task.WhenAll(Process(ca), Process(cb)).WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Empty(await Tasks(a.Id)); Assert.Empty(await Tasks(b.Id));
        Assert.Empty(await Executions(a.Id)); Assert.Empty(await Executions(b.Id));
    }

    [Fact]
    public async Task AbandonedManualAttemptExhaustsTechnicalBudgetWithoutOpeningCircuit()
    {
        var setup = await Prepare("manual"); Config.MaxAttempts = 1;
        var claim = await Claim(setup.Workspace);
        await using (var db = Db())
        {
            var runtime = Runtime(db);
            db.Add(runtime.NewAttempt(claim, await runtime.ReadAsync(claim, false, default)));
            await db.SaveChangesAsync();
        }
        await Expire(claim.Id); await Recover(setup.Workspace); await Run(setup);
        Assert.Equal("failed", (await Job(claim.Id)).StatusCode);
        var abandoned = Assert.Single(await Executions(claim.Id));
        Assert.False(abandoned.IsAutomaticAttempt); Assert.Null(abandoned.OutcomeSequence);
        await Settings(setup.Workspace, s => { s.OperatingModeCode = "automatic"; s.MaxConsecutiveFailures = 1; });
        var next = await Another(setup); await Process(await Claim(setup.Workspace));
        Assert.Single(await Tasks(next.Id));
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
