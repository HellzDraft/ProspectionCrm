using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using ProspectionCrm.Api.Data;
using ProspectionCrm.Api.Entities;

namespace ProspectionCrm.Api.Services.Automation;

public interface IAutomationJobProcessor
{
    Task ProcessAsync(AutomationJob claimed, string leaseOwner, CancellationToken token);
}

public sealed class AutomationJobProcessor(ProspectionCrmDbContext db, IAutomationJobQueue queue,
    AutomationRuntimeStore runtime, IAutomationActionExecutor executor, IOptions<AutomationWorkerOptions> options,
    ILogger<AutomationJobProcessor> logger) : IAutomationJobProcessor
{
    private bool? automaticAttempt;

    public async Task ProcessAsync(AutomationJob claimed, string leaseOwner, CancellationToken token)
    {
        automaticAttempt = null;
        using var scope = logger.BeginScope(new Dictionary<string, object?> {
            ["WorkspaceId"] = claimed.WorkspaceId, ["AutomationJobId"] = claimed.Id,
            ["AutomationRuleId"] = claimed.AutomationRuleId, ["AttemptCount"] = claimed.AttemptCount });
        Guid? executionId = null;
        try
        {
            // Initial evaluation is outside a transaction. Short locked revalidations below prevent
            // configuration/settings changes from authorizing an obsolete plan.
            await runtime.ReadAsync(claimed, false, token);
            var start = await StartAsync(claimed, leaseOwner, token);
            executionId = start.Execution;
            if (start.Complete)
                await CompleteAsync(claimed, leaseOwner, token);
            else if (executionId is { } id && await ExecuteAsync(claimed, leaseOwner, id, token))
                await CompleteAsync(claimed, leaseOwner, token);
        }
        catch (AutomationLeaseLostException)
        {
            db.ChangeTracker.Clear();
            logger.LogWarning("Automation ownership lost: {ReasonCode}", "lease-lost");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Durable running history is reconciled when the lease expires.
            throw;
        }
        catch (Exception exception)
        {
            db.ChangeTracker.Clear();
            await RecordFailureAsync(claimed, leaseOwner, IsTransient(exception), token);
        }
    }

    private async Task<(Guid? Execution, bool Complete)> StartAsync(AutomationJob claim, string owner, CancellationToken token)
    {
        await using var tx = await db.Database.BeginTransactionAsync(token);
        await runtime.LockAsync(claim.WorkspaceId, token);
        var job = await runtime.OwnAsync(claim, owner, token);
        if (job is null) return (null, false);
        await runtime.AbandonAsync(job.WorkspaceId, token);
        // A committed terminal evaluation is authoritative, even if settings or the rule later change.
        var finished = await db.AutomationExecutions.AsNoTracking().AnyAsync(x => x.AutomationJobId == job.Id
            && (x.StatusCode == "succeeded" || (x.StatusCode == "skipped" && !x.IsDeferred)), token);
        if (finished) { await tx.CommitAsync(token); return (null, true); }
        if (await db.AutomationExecutions.AnyAsync(x => x.AutomationJobId == job.Id
            && x.AttemptNumber == job.AttemptCount, token)) return (null, false);

        var input = await runtime.ReadAsync(job, true, token);
        if (input.Rule is null)
        {
            await RequireAsync(queue.FailAsync(job.WorkspaceId, job.Id, owner, AutomationJobErrors.ProcessingRejected, token));
            await tx.CommitAsync(token);
            logger.LogWarning("Automation rejected: {ReasonCode}", "automation-rule-required");
            return (null, false);
        }
        // A surviving task takes precedence over retries and safety gates: adopt, never recreate.
        var existing = await db.CrmTasks.AsNoTracking().AnyAsync(x => x.AutomationJobId == job.Id
            && x.Opportunity.WorkspaceId == job.WorkspaceId, token);
        var failures = await runtime.FailuresAsync(job.Id, token);
        if (!existing && failures >= options.Value.MaxAttempts)
        {
            var last = await db.AutomationExecutions.Where(x => x.AutomationJobId == job.Id && x.StatusCode == "failed")
                .OrderByDescending(x => x.AttemptNumber).FirstAsync(token);
            if (last.IsAutomaticAttempt)
                last.OutcomeSequence ??= await runtime.NextOutcomeAsync(job.WorkspaceId, token);
            await db.SaveChangesAsync(token);
            await RequireAsync(queue.FailAsync(job.WorkspaceId, job.Id, owner, AutomationJobErrors.JobFailed, token));
            await tx.CommitAsync(token); Log(last, "attempts-exhausted");
            return (null, false);
        }
        if (!existing && input.Settings is { } settings)
        {
            (string Reason, DateTimeOffset Until)? deferred = !settings.IsEnabled
                ? (AutomationReasons.Disabled, runtime.Now.AddSeconds(options.Value.DeferredDelaySeconds))
                : input.Evaluation?.SafetyDecision?.DecisionCode == AutomationDecisions.Automatic
                    ? await runtime.DeferredAsync(settings, token) : null;
            if (deferred is { } blocked)
            {
                await DeferAsync(job, owner, blocked, token);
                await tx.CommitAsync(token);
                return (null, false);
            }
        }
        var execution = runtime.NewAttempt(job, input);
        db.AutomationExecutions.Add(execution);
        if (existing)
        {
            execution.IsAutomaticAttempt = true;
            execution.EffectApplied = true;
            execution.OutcomeSequence = await runtime.NextOutcomeAsync(job.WorkspaceId, token);
            runtime.Finish(execution, job, "succeeded", "effect-already-applied");
        }
        await db.SaveChangesAsync(token);
        await runtime.EnsureLeaseAsync(job, owner, token);
        await tx.CommitAsync(token);
        db.ChangeTracker.Clear();
        Log(execution, execution.ReasonCode!);
        return (execution.Id, existing);
    }

    private async Task<bool> ExecuteAsync(AutomationJob claim, string owner, Guid id, CancellationToken token)
    {
        // The only executor performs local database work; no network operation is permitted
        // in this short critical section.
        await using var tx = await db.Database.BeginTransactionAsync(token);
        await runtime.LockAsync(claim.WorkspaceId, token);
        var job = await runtime.OwnAsync(claim, owner, token);
        if (job is null) throw new AutomationLeaseLostException();
        var execution = await db.AutomationExecutions.SingleAsync(x => x.Id == id && x.AutomationJobId == job.Id, token);
        if (execution.StatusCode != "running") return false;
        var input = await runtime.ReadAsync(job, true, token);
        var evaluation = input.Evaluation;
        execution.ActionTypeCode = AutomationActionCatalog.Find(input.Rule?.ActionTypeCode)?.TypeCode;
        if (input.Settings is { } settings && (!settings.IsEnabled
            || evaluation?.SafetyDecision?.DecisionCode == AutomationDecisions.Automatic))
        {
            var deferred = await runtime.DeferredAsync(settings, token);
            if (deferred is { } blocked)
            {
                // Only a change/race after the initial gates produces this one diagnostic.
                // Jobs blocked from the outset never accumulate skipped histories.
                execution.IsAutomaticAttempt = false;
                runtime.Finish(execution, job, "skipped", blocked.Reason);
                // This skipped row is a deferral, not a durable completion checkpoint.
                execution.IsDeferred = true;
                await db.SaveChangesAsync(token);
                await DeferAsync(job, owner, blocked, token);
                await tx.CommitAsync(token);
                return false;
            }
        }
        var automatic = evaluation is { IsValid: true, IsMatched: true, ActionPlan: not null }
            && evaluation.SafetyDecision?.DecisionCode == AutomationDecisions.Automatic
            && evaluation.ActionTypeCode == AutomationActionCatalog.CreateCrmTask;
        automaticAttempt = automatic;
        execution.IsAutomaticAttempt = automatic;
        if (!automatic)
            runtime.Finish(execution, job, "skipped", input.Rule is null ? "automation-rule-required"
                : input.Settings is null ? "automation-settings-missing" : evaluation!.ReasonCode);
        else
        {
            // Recheck after settings/rule locks and immediately before asking the executor to mutate.
            await runtime.EnsureLeaseAsync(job, owner, token);
            var result = await executor.ExecuteAsync(job, evaluation!.ActionPlan!, runtime.Now, token);
            runtime.Finish(execution, job, result.Succeeded ? "succeeded" : "skipped", result.ReasonCode);
            if (result.Succeeded)
            {
                execution.EffectApplied = true;
                execution.OutcomeSequence = await runtime.NextOutcomeAsync(job.WorkspaceId, token);
            }
        }
        await db.SaveChangesAsync(token);
        // A lease can expire while waiting for an Opportunity lock or during SaveChanges.
        // Throwing rolls the task and terminal history back together.
        await runtime.EnsureLeaseAsync(job, owner, token);
        await tx.CommitAsync(token);
        Log(execution, execution.ReasonCode!);
        return true;
    }

    private async Task RecordFailureAsync(AutomationJob claim, string owner, bool transient, CancellationToken token)
    {
        await using var tx = await db.Database.BeginTransactionAsync(token);
        await runtime.LockAsync(claim.WorkspaceId, token);
        var job = await runtime.OwnAsync(claim, owner, token);
        if (job is null) return;
        var execution = await db.AutomationExecutions.SingleOrDefaultAsync(
            x => x.AutomationJobId == job.Id && x.AttemptNumber == job.AttemptCount, token);
        if (execution is null)
        {
            var checkpoint = await db.AutomationExecutions.AnyAsync(x => x.AutomationJobId == job.Id
                && (x.StatusCode == "succeeded" || (x.StatusCode == "skipped" && !x.IsDeferred)), token);
            if (checkpoint)
            {
                await RequireAsync(queue.CompleteAsync(job.WorkspaceId, job.Id, owner, token));
                await tx.CommitAsync(token);
                return;
            }
            // A read/evaluation failure can precede creation of running history. Persist that
            // real technical attempt too, without retrying the evaluator that just failed.
            var rule = await db.AutomationRules.AsNoTracking().SingleOrDefaultAsync(
                x => x.Id == job.AutomationRuleId && x.WorkspaceId == job.WorkspaceId, token);
            if (rule is null)
            {
                await RequireAsync(queue.FailAsync(job.WorkspaceId, job.Id, owner, AutomationJobErrors.ProcessingRejected, token));
                await tx.CommitAsync(token);
                return;
            }
            var settings = await db.AutomationRuntimeSettings.FromSqlInterpolated($"""
                SELECT * FROM "AutomationRuntimeSettings" WHERE "WorkspaceId" = {job.WorkspaceId} FOR SHARE
                """).AsNoTracking().SingleOrDefaultAsync(token);
            if (settings is { IsEnabled: false })
            {
                await DeferAsync(job, owner, (AutomationReasons.Disabled,
                    runtime.Now.AddSeconds(options.Value.DeferredDelaySeconds)), token);
                await tx.CommitAsync(token);
                return;
            }
            execution = runtime.NewAttempt(job, new(rule, settings, null));
            db.AutomationExecutions.Add(execution);
            if (settings is null)
            {
                runtime.Finish(execution, job, "skipped", "automation-settings-missing");
                await db.SaveChangesAsync(token);
                await RequireAsync(queue.CompleteAsync(job.WorkspaceId, job.Id, owner, token));
                await tx.CommitAsync(token);
                return;
            }
        }
        // A queue completion outage must never rewrite an already committed success.
        if (execution.StatusCode != "running")
        {
            logger.LogWarning("Committed automation awaits queue recovery: {ReasonCode}", "completion-unavailable");
            return;
        }
        execution.IsAutomaticAttempt = automaticAttempt ?? execution.IsAutomaticAttempt;
        runtime.Finish(execution, job, "failed", transient ? "transient-failure" : "technical-failure");
        var failures = await runtime.FailuresAsync(job.Id, token) + 1;
        var retry = transient && failures < options.Value.MaxAttempts;
        if (!retry && execution.IsAutomaticAttempt)
            execution.OutcomeSequence = await runtime.NextOutcomeAsync(job.WorkspaceId, token);
        await db.SaveChangesAsync(token);
        if (retry)
            await RequireAsync(queue.ReleaseAsync(job.WorkspaceId, job.Id, owner, runtime.Now + options.Value.Backoff(failures), token));
        else
            await RequireAsync(queue.FailAsync(job.WorkspaceId, job.Id, owner, AutomationJobErrors.JobFailed, token));
        await tx.CommitAsync(token);
        Log(execution, execution.ReasonCode!);
    }

    private async Task DeferAsync(AutomationJob job, string owner, (string Reason, DateTimeOffset Until) deferred, CancellationToken token)
    {
        await RequireAsync(queue.ReleaseAsync(job.WorkspaceId, job.Id, owner, deferred.Until, token));
        // Each job is delayed, so these messages cannot form an idle-loop log flood.
        if (deferred.Reason == "circuit-open")
            logger.LogWarning("Automation postponed until {AvailableAt}: {ReasonCode}", deferred.Until, deferred.Reason);
        else logger.LogDebug("Automation postponed until {AvailableAt}: {ReasonCode}", deferred.Until, deferred.Reason);
    }

    private async Task CompleteAsync(AutomationJob job, string owner, CancellationToken token)
    {
        if (!await queue.CompleteAsync(job.WorkspaceId, job.Id, owner, token))
            logger.LogWarning("Committed automation awaits reconciliation: {ReasonCode}", "lease-lost");
    }
    private void Log(AutomationExecution execution, string reason) => logger.LogInformation(
        "Automation execution {AutomationExecutionId} is {StatusCode}: {ReasonCode}", execution.Id, execution.StatusCode, reason);
    private static async Task RequireAsync(Task<bool> operation)
    {
        if (!await operation) throw new AutomationLeaseLostException();
    }
    private static bool IsTransient(Exception error) => error switch
    {
        PostgresException pg => pg.SqlState is PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.DeadlockDetected,
        NpgsqlException pg => pg.IsTransient,
        DbUpdateException { InnerException: { } inner } => IsTransient(inner),
        _ => false
    };
}
