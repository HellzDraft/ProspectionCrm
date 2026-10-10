using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ProspectionCrm.Api.Data;
using ProspectionCrm.Api.Dtos.AutomationSupervision;

namespace ProspectionCrm.Api.Services.Automation;

// No locks, transactions, SaveChanges, recovery, claim or implicit settings creation.
public sealed class AutomationSupervisionService(ProspectionCrmDbContext db, AutomationSupervisionWorkspace workspace,
    AutomationRuntimeGuard guard, TimeProvider clock, IOptions<AutomationWorkerOptions> worker,
    IOptions<AutomationSupervisionOptions> options)
{
    public async Task<AutomationJobResult<AutomationSupervisionDto>> ReadAsync(CancellationToken token)
    {
        var resolved = await workspace.ResolveAsync(token);
        if (resolved.Value is not { } settings) return new(null, resolved.Status, resolved.Code);
        var id = settings.WorkspaceId; var now = clock.GetUtcNow().ToUniversalTime(); var since = now.AddHours(-24);
        var queue = await db.Database.SqlQuery<AutomationQueueSummary>($"""
            SELECT
                count(*) FILTER (WHERE "StatusCode" = 'pending' AND "AvailableAt" <= {now}) AS "PendingAvailableCount",
                count(*) FILTER (WHERE "StatusCode" = 'pending' AND "AvailableAt" > {now}) AS "PendingScheduledCount",
                count(*) FILTER (WHERE "StatusCode" = 'leased' AND "LeaseExpiresAt" > statement_timestamp()) AS "LeasedActiveCount",
                count(*) FILTER (WHERE "StatusCode" = 'leased' AND "LeaseExpiresAt" <= statement_timestamp()) AS "LeasedExpiredCount",
                count(*) FILTER (WHERE "StatusCode" = 'awaiting-approval') AS "AwaitingApprovalCount",
                count(*) FILTER (WHERE "StatusCode" = 'failed') AS "FailedCount",
                count(*) FILTER (WHERE "StatusCode" = 'cancelled') AS "CancelledCount",
                count(*) FILTER (WHERE "StatusCode" = 'completed' AND "CompletedAt" >= {since} AND "CompletedAt" <= {now}) AS "CompletedLast24HoursCount",
                min("CreatedAt") FILTER (WHERE "StatusCode" IN ('pending','leased','awaiting-approval')) AS "OldestOpenJobAt",
                min("AvailableAt") FILTER (WHERE "StatusCode" = 'pending' AND "AvailableAt" <= {now}) AS "OldestPendingAvailableAt",
                min("AvailableAt") FILTER (WHERE "StatusCode" = 'pending' AND "AvailableAt" > {now}) AS "NextAvailableAt",
                max(COALESCE("UpdatedAt","CreatedAt")) AS "LastJobUpdatedAt"
            FROM "AutomationJobs" WHERE "WorkspaceId" = {id}
            """).SingleAsync(token);
        var requests = await db.Database.SqlQuery<AutomationRequestSummary>($"""
            SELECT count(*) FILTER (WHERE "StatusCode" = 'pending') AS "PendingCount",
                0::bigint AS "PendingStaleCount",
                count(*) FILTER (WHERE "StatusCode" = 'approved' AND "DecidedAt" >= {since} AND "DecidedAt" <= {now}) AS "ApprovedLast24HoursCount",
                count(*) FILTER (WHERE "StatusCode" = 'rejected' AND "DecidedAt" >= {since} AND "DecidedAt" <= {now}) AS "RejectedLast24HoursCount",
                min("RequestedAt") FILTER (WHERE "StatusCode" = 'pending') AS "OldestPendingAt",
                max("DecidedAt") AS "LastDecisionAt"
            FROM "AutomationActionRequests" WHERE "WorkspaceId" = {id}
            """).SingleAsync(token);
        long stale = 0;
        // Stream the minimal relevant entity pair; never retain the backlog or issue N+1 queries.
        var pending = from request in db.AutomationActionRequests.AsNoTracking()
            where request.WorkspaceId == id && request.StatusCode == ActionRequestStatuses.Pending
            join rule in db.AutomationRules.AsNoTracking().Where(x => x.WorkspaceId == id)
                on request.AutomationRuleId equals rule.Id into rules
            from rule in rules.DefaultIfEmpty()
            select new { Request = request, Rule = rule };
        await foreach (var row in pending.AsAsyncEnumerable().WithCancellation(token))
            if (AutomationActionSnapshot.IsStale(row.Request, row.Rule)) stale++;
        requests = requests with { PendingStaleCount = stale };

        var failureSince = now.AddHours(-options.Value.RecentFailureWindowHours);
        var executions = await db.Database.SqlQuery<AutomationExecutionSummary>($"""
            SELECT count(*) FILTER (WHERE "StatusCode" = 'running') AS "RunningCount",
                0::bigint AS "AbandonedRunningCount",
                count(*) FILTER (WHERE "StatusCode" = 'succeeded' AND "FinishedAt" >= {since} AND "FinishedAt" <= {now}) AS "SucceededLast24HoursCount",
                count(*) FILTER (WHERE "StatusCode" = 'failed' AND "FinishedAt" >= {since} AND "FinishedAt" <= {now}) AS "FailedLast24HoursCount",
                count(*) FILTER (WHERE "StatusCode" = 'skipped' AND "FinishedAt" >= {since} AND "FinishedAt" <= {now}) AS "SkippedLast24HoursCount",
                count(*) FILTER (WHERE "IsAutomaticAttempt" AND "EffectApplied" AND "FinishedAt" >= {since} AND "FinishedAt" <= {now}) AS "AutomaticEffectsLast24HoursCount",
                count(*) FILTER (WHERE "IsHumanApprovedAttempt" AND "EffectApplied" AND "FinishedAt" >= {since} AND "FinishedAt" <= {now}) AS "HumanEffectsLast24HoursCount",
                count(*) FILTER (WHERE "StatusCode" = 'failed' AND "FinishedAt" >= {failureSince} AND "FinishedAt" <= {now}) AS "RecentTechnicalFailuresCount",
                max("TriggeredAt") AS "LastExecutionAt",
                max("FinishedAt") FILTER (WHERE "EffectApplied") AS "LastEffectAt"
            FROM "AutomationExecutions" WHERE "WorkspaceId" = {id}
            """).SingleAsync(token);
        executions = executions with { AbandonedRunningCount =
            await AutomationExecutionQueries.Abandoned(db, id).AsNoTracking().LongCountAsync(token) };
        var state = await guard.ReadAsync(settings, now, token); var config = worker.Value;
        var snapshot = new AutomationSupervisionDto(id, now,
            new(config.Enabled, config.IdleDelaySeconds, config.RecoveryIntervalSeconds, config.MaxAttempts),
            new(settings.IsEnabled, settings.OperatingModeCode, settings.MaxExecutionsPerMinute,
                settings.MaxExecutionsPerDay, settings.MaxConsecutiveFailures),
            queue, requests, executions, state.Quotas, state.Circuit, []);
        return new(snapshot with { Alerts = AutomationAlerts.Calculate(snapshot, options.Value) });
    }
}
