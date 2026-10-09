using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using ProspectionCrm.Api.Entities;
using ProspectionCrm.Api.Services.Automation;
using Xunit;

namespace ProspectionCrm.Api.Tests;

public sealed class AutomationRuntimeBehaviorTests(AutomationJobDatabase database)
    : AutomationRuntimeFixture(database), IClassFixture<AutomationJobDatabase>
{
    [Theory]
    [InlineData("automatic", "task-created", "succeeded", 1)]
    public async Task ModesApplyOnlyAutomaticEffects(string mode, string reason, string status, int tasks)
    {
        var setup = await Prepare(mode);
        await Run(setup);
        Assert.Equal("completed", (await Job(setup.Job.Id)).StatusCode);
        var execution = Assert.Single(await Executions(setup.Job.Id));
        Assert.Equal(status, execution.StatusCode); Assert.Equal(reason, execution.ReasonCode);
        Assert.Equal(setup.Workspace, execution.WorkspaceId); Assert.Equal(setup.Rule.Id, execution.AutomationRuleId);
        Assert.Equal(1, execution.AttemptNumber); Assert.Equal(Clock.Now, execution.TriggeredAt);
        Assert.Equal(Clock.Now, execution.StartedAt); Assert.Equal(Clock.Now, execution.FinishedAt);
        Assert.Equal(tasks == 1, execution.EffectApplied); Assert.Equal(tasks, (await Tasks(setup.Job.Id)).Count);
        Assert.DoesNotContain("Texte fixe", execution.ContextJson);
        Assert.Equal(reason, JsonDocument.Parse(execution.ContextJson!).RootElement.GetProperty("reasonCode").GetString());
        await using var db = Db();
        Assert.Empty(await db.ActivityEntries.Where(x => x.WorkspaceId == setup.Workspace).ToListAsync());
    }

    [Fact]
    public async Task KillSwitchRepeatedClaimsDoNotConsumeTechnicalBudgetOrCreateHistory()
    {
        var setup = await Prepare(enabled: false);
        for (var i = 0; i < 5; i++)
        {
            await Run(setup);
            var job = await Job(setup.Job.Id); Assert.Equal("pending", job.StatusCode);
            Assert.Equal(Clock.Now.AddSeconds(60), job.AvailableAt); Assert.Equal(i + 1, job.AttemptCount);
            Assert.Empty(await Executions(job.Id)); Assert.Empty(await Tasks(job.Id)); await Available(job.Id);
        }
        await Settings(setup.Workspace, s => s.IsEnabled = true);
        await Run(setup);
        Assert.Single(await Tasks(setup.Job.Id));
        Assert.Equal(6, Assert.Single(await Executions(setup.Job.Id)).AttemptNumber);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(5)]
    public async Task TaskMappingUsesExecutionUtcClock(int? days)
    {
        var setup = await Prepare(days: days); await Run(setup);
        var task = Assert.Single(await Tasks(setup.Job.Id));
        Assert.Equal(setup.Opportunity.Id, task.OpportunityId); Assert.Equal("Relancer", task.Title);
        Assert.Equal("Texte fixe", task.Description); Assert.Equal(Clock.Now, task.CreatedAt);
        Assert.Equal(days is { } n ? Clock.Now.AddDays(n) : (DateTimeOffset?)null, task.DueAt);
        Assert.Equal(TimeSpan.Zero, task.CreatedAt.Offset); Assert.False(task.IsCompleted); Assert.Null(task.CompletedAt);
    }

    [Theory]
    [InlineData("disabled", "rule-disabled")]
    [InlineData("archived", "rule-archived")]
    [InlineData("condition", "condition-not-matched")]
    [InlineData("pipeline", "pipeline-mismatch")]
    [InlineData("context", "invalid-context")]
    [InlineData("configuration", "invalid-action-configuration")]
    [InlineData("action", "unsupported-action")]
    [InlineData("trigger", "unsupported-trigger")]
    [InlineData("category", "action-category-mismatch")]
    [InlineData("target-id", "invalid-opportunity-id")]
    [InlineData("target-missing", "opportunity-not-found")]
    [InlineData("target-foreign", "opportunity-not-found")]
    [InlineData("target-archived", "opportunity-archived")]
    [InlineData("settings", "automation-settings-missing")]
    public async Task NonMatchesAreTerminalAndNeverRetried(string change, string reason)
    {
        var setup = await Prepare();
        switch (change)
        {
            case "disabled": await ChangeRule(setup.Rule.Id, r => r.Enabled = false); break;
            case "archived": await ChangeRule(setup.Rule.Id, r => r.ArchivedAt = Clock.Now); break;
            case "condition": await ChangeRule(setup.Rule.Id, r => r.ConditionJson = """{"type":"context-equals","property":"source","value":"other"}"""); break;
            case "pipeline":
                var pipeline = await Pipeline(setup.Workspace);
                await ChangeRule(setup.Rule.Id, r => r.PipelineId = pipeline); break;
            case "configuration": await ChangeRule(setup.Rule.Id, r => r.ActionConfigurationJson = "{}"); break;
            case "action": await ChangeRule(setup.Rule.Id, r => r.ActionTypeCode = "secret-marker"); break;
            case "trigger": await ChangeRule(setup.Rule.Id, r => r.TriggerTypeCode = "secret-marker"); break;
            default:
                await using (var db = Db())
                {
                    if (change == "settings")
                        await db.AutomationRuntimeSettings.Where(x => x.WorkspaceId == setup.Workspace).ExecuteDeleteAsync();
                    else if (change == "target-archived")
                        await db.Opportunities.Where(x => x.Id == setup.Opportunity.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.ArchivedAt, Clock.Now));
                    else
                    {
                        var job = await db.AutomationJobs.SingleAsync(x => x.Id == setup.Job.Id);
                        if (change == "category") job.ActionCategoryCode = "email";
                        if (change == "context") job.ContextJson = "{}";
                        if (change == "target-id") job.ContextJson = """{"eventKey":"test","pipelineId":null,"payload":{"opportunityId":"invalid"}}""";
                        if (change == "target-missing") job.ContextJson = Context(Guid.NewGuid());
                        if (change == "target-foreign") job.ContextJson = Context((await Prepare()).Opportunity.Id);
                        await db.SaveChangesAsync();
                    }
                }
                break;
        }
        await Run(setup);
        var execution = Assert.Single(await Executions(setup.Job.Id));
        Assert.Equal("skipped", execution.StatusCode); Assert.Equal(reason, execution.ReasonCode);
        Assert.False(execution.EffectApplied); Assert.Null(execution.OutcomeSequence);
        Assert.DoesNotContain("secret-marker", execution.ContextJson);
        Assert.Equal("completed", (await Job(setup.Job.Id)).StatusCode); Assert.Empty(await Tasks(setup.Job.Id));
        await using var check = Db(); Assert.Null(await Queue(check).ClaimAsync(setup.Workspace, default));
    }

    [Fact]
    public async Task UntargetedJobIsControlledRejectionWithoutInventingARule()
    {
        var ws = await Workspace(); var job = await Add(ws);
        await Process(await Claim(ws)); var result = await Job(job.Id);
        Assert.Equal("failed", result.StatusCode); Assert.Equal(AutomationJobErrors.ProcessingRejected, result.LastError);
        Assert.Empty(await Executions(job.Id)); Assert.Empty(await Tasks(job.Id));
    }

    [Fact]
    public async Task RunningHistoryIsCommittedBeforeTheExecutorStarts()
    {
        var setup = await Prepare();
        await Run(setup, (db, runtime) => new DelegateExecutor(async (job, plan, now, token) =>
        {
            var running = Assert.Single(await Executions(job.Id));
            Assert.Equal("running", running.StatusCode); Assert.Null(running.FinishedAt);
            Assert.Empty(await Tasks(job.Id));
            return await new CreateCrmTaskAutomationExecutor(db, runtime).ExecuteAsync(job, plan, now, token);
        }));
        Assert.Equal("succeeded", Assert.Single(await Executions(setup.Job.Id)).StatusCode);
    }

    [Fact]
    public async Task ConcurrentProcessorsAndReplayCannotDuplicateOneClaim()
    {
        var setup = await Prepare(); var claim = await Claim(setup.Workspace);
        await Task.WhenAll(Process(claim), Process(claim)).WaitAsync(TimeSpan.FromSeconds(20));
        await Process(claim);
        Assert.Single(await Tasks(claim.Id)); Assert.Single(await Executions(claim.Id));
        Assert.Equal("completed", (await Job(claim.Id)).StatusCode);
    }

    [Fact]
    public async Task InvalidOwnerAndExpiredLeaseCannotStartAnAttempt()
    {
        var setup = await Prepare(); var claim = await Claim(setup.Workspace);
        await Process(claim, owner: "foreign-owner"); Assert.Empty(await Executions(claim.Id));
        await Expire(claim.Id); await Process(claim);
        Assert.Empty(await Tasks(claim.Id)); Assert.Empty(await Executions(claim.Id));
    }

    [Fact]
    public async Task ExpiryDuringMutationRollsBackTaskAndTerminalHistory()
    {
        var setup = await Prepare(); var claim = await Claim(setup.Workspace);
        await Process(claim, (db, runtime) => new DelegateExecutor(async (job, plan, now, token) =>
        {
            var result = await new CreateCrmTaskAutomationExecutor(db, runtime).ExecuteAsync(job, plan, now, token);
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE \"AutomationJobs\" SET \"LeaseExpiresAt\" = clock_timestamp() - interval '1 second' WHERE \"Id\" = {job.Id}", token);
            return result;
        }));
        Assert.Empty(await Tasks(claim.Id));
        Assert.Equal("running", Assert.Single(await Executions(claim.Id)).StatusCode);
        await Expire(claim.Id); Assert.Equal(1, await Recover(setup.Workspace));
        Assert.Equal("lease-expired", Assert.Single(await Executions(claim.Id)).ReasonCode);
        await Run(setup); Assert.Single(await Tasks(claim.Id));
    }

    [Fact]
    public async Task CancellationLeavesRecoverableHistoryAndOldOwnerCannotFinishNewAttempt()
    {
        var setup = await Prepare(); var claim = await Claim(setup.Workspace);
        using var stop = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Process(claim, (_, _) => new DelegateExecutor((_, _, _, token) =>
        { stop.Cancel(); token.ThrowIfCancellationRequested(); throw new InvalidOperationException(); }), token: stop.Token));
        await Expire(claim.Id); Assert.Equal(1, await Recover(setup.Workspace)); Assert.Equal(0, await Recover(setup.Workspace));
        var next = await Claim(setup.Workspace); await Process(claim); Assert.Empty(await Tasks(claim.Id));
        await Process(next);
        var histories = await Executions(claim.Id); Assert.Equal(2, histories.Count);
        Assert.Equal("failed", histories[0].StatusCode); Assert.Equal("lease-expired", histories[0].ReasonCode);
        Assert.NotNull(histories[0].FinishedAt); Assert.Equal("succeeded", histories[1].StatusCode);
        Assert.Single(await Tasks(claim.Id));
    }

    [Fact]
    public async Task PostgreSqlRejectsDuplicateTaskAndAttemptEvenForDirectWriters()
    {
        var setup = await Prepare(); await Run(setup);
        await using var db = Db();
        db.Add(new CrmTask { OpportunityId = setup.Opportunity.Id, AutomationJobId = setup.Job.Id, Title = "Duplicate" });
        var duplicate = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Equal("23505", Assert.IsType<PostgresException>(duplicate.InnerException).SqlState);
        db.ChangeTracker.Clear();
        var copy = Assert.Single(await Executions(setup.Job.Id)); copy.Id = Guid.NewGuid();
        copy.EffectApplied = false; copy.OutcomeSequence = null; db.Add(copy);
        duplicate = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        var pg = Assert.IsType<PostgresException>(duplicate.InnerException);
        Assert.Equal("23505", pg.SqlState); Assert.Equal("UX_AutomationExecutions_Job_Attempt", pg.ConstraintName);
    }

    private sealed class InspectingEvaluator(Action inspect) : IAutomationRuleEvaluator
    {
        private int calls;
        public AutomationEvaluationResult Evaluate(AutomationJob job, AutomationRule rule, AutomationRuntimeSettings settings)
        {
            if (Interlocked.Increment(ref calls) == 1) inspect();
            return new AutomationRuleEvaluator(new AutomationSafetyPolicy()).Evaluate(job, rule, settings);
        }
    }

    [Fact]
    public async Task RuleChangeAfterInitialSnapshotIsRevalidatedBeforeMutation()
    {
        var setup = await Prepare(); var claim = await Claim(setup.Workspace);
        var evaluator = new InspectingEvaluator(() => ChangeRule(setup.Rule.Id, r => r.Enabled = false).GetAwaiter().GetResult());
        await Process(claim, evaluator: evaluator);
        Assert.Empty(await Tasks(claim.Id));
        Assert.Equal("rule-disabled", Assert.Single(await Executions(claim.Id)).ReasonCode);
    }

    [Fact]
    public async Task LeaseExpiredWhileWaitingForWorkspaceLockCannotAuthorizeMutation()
    {
        var setup = await Prepare(); var claim = await Claim(setup.Workspace);
        await using var blocker = Db(); await using var tx = await blocker.Database.BeginTransactionAsync();
        await Runtime(blocker).LockAsync(setup.Workspace, default);
        var inspected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var processing = Process(claim, evaluator: new InspectingEvaluator(() => inspected.TrySetResult()));
        await inspected.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Expire(claim.Id); await tx.CommitAsync();
        await processing.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Empty(await Tasks(claim.Id)); Assert.Empty(await Executions(claim.Id));
    }

    [Fact]
    public async Task DatabasePreventsCrossWorkspaceHistoryAndDuplicateLogicalEffect()
    {
        var setup = await Prepare(); await Run(setup); var other = await Prepare();
        await using var db = Db();
        var copy = Assert.Single(await Executions(setup.Job.Id));
        copy.Id = Guid.NewGuid(); copy.AttemptNumber = 99; copy.OutcomeSequence = 99;
        copy.WorkspaceId = other.Workspace; copy.AutomationRuleId = other.Rule.Id;
        copy.EffectApplied = false; copy.OutcomeSequence = null;
        db.Add(copy);
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Equal("23503", Assert.IsType<PostgresException>(error.InnerException).SqlState);
        db.ChangeTracker.Clear();
        copy.WorkspaceId = setup.Workspace; copy.AutomationRuleId = setup.Rule.Id;
        copy.EffectApplied = true; copy.OutcomeSequence = 99;
        db.Add(copy);
        error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        var pg = Assert.IsType<PostgresException>(error.InnerException);
        Assert.Equal("23505", pg.SqlState); Assert.Equal("UX_AutomationExecutions_Job_Effect", pg.ConstraintName);
    }
}
