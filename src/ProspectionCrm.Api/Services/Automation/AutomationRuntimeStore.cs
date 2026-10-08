using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ProspectionCrm.Api.Data;
using ProspectionCrm.Api.Entities;

namespace ProspectionCrm.Api.Services.Automation;

public sealed class AutomationLeaseLostException : Exception { }

public sealed record AutomationRuntimeInput(AutomationRule? Rule, AutomationRuntimeSettings? Settings,
    AutomationEvaluationResult? Evaluation);

public sealed class AutomationRuntimeStore(ProspectionCrmDbContext db, IAutomationRuleEvaluator evaluator,
    TimeProvider clock, IOptions<AutomationWorkerOptions> options)
{
    public DateTimeOffset Now => clock.GetUtcNow().ToUniversalTime();
    public Task LockAsync(Guid workspace, CancellationToken token)
    {
        var key = $"automation-runtime:{workspace:N}";
        return db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({key}, 0))", token);
    }

    public async Task<AutomationJob?> OwnAsync(AutomationJob claim, string owner, CancellationToken token)
    {
        var job = await db.AutomationJobs.FromSqlInterpolated($"""
            SELECT * FROM "AutomationJobs" WHERE "Id" = {claim.Id} AND "WorkspaceId" = {claim.WorkspaceId}
            FOR UPDATE
            """).AsNoTracking().SingleOrDefaultAsync(token);
        if (job is null || job.StatusCode != AutomationJobStatuses.Leased || job.LeaseOwner != owner
            || job.AttemptCount != claim.AttemptCount) return null;
        return await ValidLeaseAsync(job, owner, token) ? job : null;
    }

    public Task<bool> ValidLeaseAsync(AutomationJob job, string owner, CancellationToken token) =>
        db.Database.SqlQuery<bool>($"""
            SELECT EXISTS(SELECT 1 FROM "AutomationJobs" WHERE "Id" = {job.Id}
                AND "WorkspaceId" = {job.WorkspaceId} AND "StatusCode" = 'leased'
                AND "LeaseOwner" = {owner} AND "AttemptCount" = {job.AttemptCount}
                AND "LeaseExpiresAt" > clock_timestamp()) AS "Value"
            """).SingleAsync(token);

    public async Task EnsureLeaseAsync(AutomationJob job, string owner, CancellationToken token)
    {
        if (!await ValidLeaseAsync(job, owner, token)) throw new AutomationLeaseLostException();
    }

    public async Task<AutomationRuntimeInput> ReadAsync(AutomationJob job, bool locked, CancellationToken token)
    {
        var rule = locked
            ? await db.AutomationRules.FromSqlInterpolated($"""
                SELECT * FROM "AutomationRules" WHERE "Id" = {job.AutomationRuleId}
                    AND "WorkspaceId" = {job.WorkspaceId} FOR SHARE
                """).AsNoTracking().SingleOrDefaultAsync(token)
            : await db.AutomationRules.AsNoTracking().SingleOrDefaultAsync(
                x => x.Id == job.AutomationRuleId && x.WorkspaceId == job.WorkspaceId, token);
        var settings = locked
            ? await db.AutomationRuntimeSettings.FromSqlInterpolated($"""
                SELECT * FROM "AutomationRuntimeSettings" WHERE "WorkspaceId" = {job.WorkspaceId} FOR SHARE
                """).AsNoTracking().SingleOrDefaultAsync(token)
            : await db.AutomationRuntimeSettings.AsNoTracking().SingleOrDefaultAsync(x => x.WorkspaceId == job.WorkspaceId, token);
        return new(rule, settings, rule is not null && settings is not null ? evaluator.Evaluate(job, rule, settings) : null);
    }

    public Task<int> FailuresAsync(Guid job, CancellationToken token) => db.AutomationExecutions
        .CountAsync(x => x.AutomationJobId == job && x.StatusCode == "failed", token);

    public async Task<long> NextOutcomeAsync(Guid workspace, CancellationToken token) =>
        (await db.AutomationExecutions.Where(x => x.WorkspaceId == workspace)
            .MaxAsync(x => x.OutcomeSequence, token) ?? 0) + 1;

    // Must be called under the workspace advisory lock, and repeated in the mutation transaction.
    public async Task<(string Reason, DateTimeOffset Until)?> DeferredAsync(
        AutomationRuntimeSettings settings, CancellationToken token)
    {
        var now = Now;
        if (!settings.IsEnabled) return (AutomationReasons.Disabled, now.AddSeconds(options.Value.DeferredDelaySeconds));
        var outcomes = await db.AutomationExecutions.AsNoTracking()
            .Where(x => x.WorkspaceId == settings.WorkspaceId && x.OutcomeSequence != null)
            .OrderByDescending(x => x.OutcomeSequence).Take(settings.MaxConsecutiveFailures)
            .Select(x => x.EffectApplied).ToListAsync(token);
        if (outcomes.Count == settings.MaxConsecutiveFailures && outcomes.All(x => !x))
            return ("circuit-open", now.AddSeconds(options.Value.DeferredDelaySeconds));
        var day = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
        var effects = db.AutomationExecutions.AsNoTracking()
            .Where(x => x.WorkspaceId == settings.WorkspaceId && x.EffectApplied);
        var dayCount = await effects.CountAsync(x => x.FinishedAt >= day && x.FinishedAt < day.AddDays(1), token);
        if (dayCount >= settings.MaxExecutionsPerDay) return ("daily-quota", day.AddDays(1));
        var start = now.AddMinutes(-1);
        var recent = await effects.Where(x => x.FinishedAt > start).OrderBy(x => x.FinishedAt)
            .Select(x => x.FinishedAt!.Value).ToListAsync(token);
        if (recent.Count >= settings.MaxExecutionsPerMinute)
            return ("minute-quota", recent[recent.Count - settings.MaxExecutionsPerMinute].AddMinutes(1));
        return null;
    }

    public AutomationExecution NewAttempt(AutomationJob job, AutomationRuntimeInput input)
    {
        var now = Now;
        var execution = new AutomationExecution { WorkspaceId = job.WorkspaceId, AutomationRuleId = job.AutomationRuleId!.Value,
            AutomationJobId = job.Id, AttemptNumber = job.AttemptCount, StatusCode = "running",
            TriggeredAt = now, StartedAt = now,
            ActionTypeCode = AutomationActionCatalog.Find(input.Rule?.ActionTypeCode)?.TypeCode,
            IsAutomaticAttempt = input.Evaluation?.SafetyDecision?.DecisionCode == AutomationDecisions.Automatic };
        Describe(execution, job, input.Evaluation?.ReasonCode ?? "automation-settings-missing");
        return execution;
    }

    public void Finish(AutomationExecution execution, AutomationJob job, string status, string reason)
    {
        execution.StatusCode = status;
        var now = Now;
        execution.FinishedAt = execution.StartedAt is { } started && now < started ? started : now;
        execution.ErrorMessage = status == "failed" ? reason : null;
        Describe(execution, job, reason);
    }

    public static void Describe(AutomationExecution execution, AutomationJob job, string reason)
    {
        execution.ReasonCode = reason;
        execution.ContextJson = JsonSerializer.Serialize(new {
            automationJobId = job.Id, attemptNumber = execution.AttemptNumber,
            triggerTypeCode = job.TriggerTypeCode, actionTypeCode = execution.ActionTypeCode,
            actionCategoryCode = job.ActionCategoryCode, reasonCode = reason
        });
    }

    public async Task<int> AbandonAsync(Guid workspace, CancellationToken token)
    {
        var abandoned = await (from execution in db.AutomationExecutions
            join job in db.AutomationJobs on execution.AutomationJobId equals job.Id
            where execution.WorkspaceId == workspace && execution.StatusCode == "running"
                && (job.StatusCode != AutomationJobStatuses.Leased || execution.AttemptNumber != job.AttemptCount)
            orderby execution.StartedAt, execution.Id
            select new { Execution = execution, Job = job }).Take(100).ToListAsync(token);
        foreach (var item in abandoned) Finish(item.Execution, item.Job, "failed", "lease-expired");
        await db.SaveChangesAsync(token);
        return abandoned.Count;
    }
}
