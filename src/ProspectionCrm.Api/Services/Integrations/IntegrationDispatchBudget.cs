using Microsoft.EntityFrameworkCore;
using ProspectionCrm.Api.Data;

namespace ProspectionCrm.Api.Services.Integrations;

public sealed class IntegrationDispatchLimits
{
    public bool Enabled { get; set; }
    public int MaxStartsPerMinute { get; set; } = 60;
    public int MaxStartsPerDay { get; set; } = 1000;
    public bool IsValid() => MaxStartsPerMinute is >= 1 and <= 10000 && MaxStartsPerDay is >= 1 and <= 1000000;
}

internal static class IntegrationDispatchBudget
{
    // Caller holds AutomationRuntimeLock for this workspace in the same READ COMMITTED
    // transaction as the attempt INSERT. Never acquire the send gate or perform network IO here.
    internal static async Task<(DateTimeOffset Now, string? Refusal)> CheckUnderRuntimeLockAsync(
        ProspectionCrmDbContext db, Guid workspace, Guid connection, IntegrationDispatchLimits limits, CancellationToken token)
    {
        if (db.Database.CurrentTransaction is null) throw new InvalidOperationException("dispatch-transaction-required");
        var now = await IntegrationSyncLocks.Now(db, token);
        if (!limits.IsValid()) throw new InvalidOperationException("invalid-dispatch-limits");
        if (!limits.Enabled) return (now, null);
        var usage = await ReadUsageAsync(db, workspace, connection, now, token);
        return (now, usage.Minute >= limits.MaxStartsPerMinute ? "IntegrationDispatchMinuteLimitReached"
            : usage.Day >= limits.MaxStartsPerDay ? "IntegrationDispatchDayLimitReached" : null);
    }

    internal sealed class Usage
    {
        public long Minute { get; set; }
        public long Day { get; set; }
    }

    internal static Task<Usage> ReadUsageAsync(ProspectionCrmDbContext db, Guid workspace, Guid connection,
        DateTimeOffset now, CancellationToken token) => db.Database.SqlQuery<Usage>($"""
        SELECT count(*) FILTER (WHERE a."StartedAt" > {now} - interval '1 minute') AS "Minute",
               count(*) FILTER (WHERE a."StartedAt" >= date_trunc('day', {now} AT TIME ZONE 'UTC') AT TIME ZONE 'UTC') AS "Day"
        FROM "IntegrationAttempts" a JOIN "IntegrationOperations" o ON o."Id" = a."OperationId"
        WHERE o."WorkspaceId" = {workspace} AND o."ConnectionId" = {connection}
          AND o."Type" IN ('email.send', 'calendar.event.create', 'calendar.event.update')
          AND a."StartedAt" <= {now}
          AND a."StartedAt" >= LEAST({now} - interval '1 minute', date_trunc('day', {now} AT TIME ZONE 'UTC') AT TIME ZONE 'UTC')
        """).SingleAsync(token);
}
