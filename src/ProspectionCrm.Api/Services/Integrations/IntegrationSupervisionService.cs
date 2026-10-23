using System.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using ProspectionCrm.Api.Data;
using ProspectionCrm.Api.Dtos.Integrations;

namespace ProspectionCrm.Api.Services.Integrations;

// Metadata only. No dispatcher, processor, circuit service or network dependency.
public sealed class IntegrationSupervisionService(ProspectionCrmDbContext db, ICurrentWorkspaceProvider workspace, TimeProvider clock)
{
    public const int OldAttemptMinutes = 5;
    public const int OldWaitingMinutes = 30;
    public static readonly IReadOnlySet<string> Types = new HashSet<string>(StringComparer.Ordinal)
        { "technical.probe", "collection.request", "email.sync", "email.send", "calendar.sync", "calendar.event.create", "calendar.event.update" };
    public static readonly IReadOnlySet<string> Statuses = new HashSet<string>(StringComparer.Ordinal)
        { "pending", "awaiting-result", "result-received", "succeeded", "failed" };
    public static readonly IReadOnlySet<string> States = new HashSet<string>(StringComparer.Ordinal)
        { "waiting", "uncertain", "evidence-pending", "conflict", "failed", "completed", "diagnostic-required" };

    private IQueryable<IntegrationOperationView> Query(Guid id, DateTimeOffset now) =>
        db.Database.SqlQueryRaw<IntegrationOperationView>(IntegrationSupervisionQuery.Sql,
            new NpgsqlParameter("workspace", id), new NpgsqlParameter("oldAttempt", now.AddMinutes(-OldAttemptMinutes)),
            new NpgsqlParameter("oldWaiting", now.AddMinutes(-OldWaitingMinutes)));

    public async Task<IntegrationSupervisionSnapshot> SnapshotAsync(CancellationToken token)
    {
        var id = await workspace.GetCurrentWorkspaceIdAsync(token); var now = clock.GetUtcNow();
        // A coherent snapshot, with PostgreSQL itself rejecting accidental writes in this transaction.
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, token);
        await db.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY", token);
        var groups = await Query(id, now).GroupBy(x => new { x.Type, x.StatusCode, x.State })
            .OrderBy(g => g.Key.Type).ThenBy(g => g.Key.StatusCode).ThenBy(g => g.Key.State)
            .Select(g => new IntegrationSupervisionGroup(g.Key.Type, g.Key.StatusCode, g.Key.State, g.LongCount(),
                g.Sum(x => x.ActiveAttemptCount), g.Sum(x => x.OldActiveAttemptCount), g.Sum(x => x.UnprocessedReceiptCount),
                g.Sum(x => x.OldUnprocessedReceiptCount), g.Min(x => x.OldestActiveAttemptAt),
                g.Min(x => x.OldestUnprocessedReceiptAt), g.Min(x => x.WaitingSince), g.Min(x => x.UncertainSince)))
            .ToListAsync(token);
        var oldWaits = await Query(id, now).LongCountAsync(x => x.WaitingSince <= now.AddMinutes(-OldWaitingMinutes), token);
        await tx.CommitAsync(token);
        var alerts = new List<IntegrationSupervisionAlert>();
        void Add(string level, string code, long count, DateTimeOffset? oldest = null)
        { if (count > 0) alerts.Add(new(level, code, count, oldest)); }
        Add("warning", "IntegrationAttemptOld", groups.Sum(x => x.OldActiveAttemptCount),
            groups.Where(x => x.OldActiveAttemptCount > 0).Min(x => x.OldestActiveAttemptAt));
        Add("warning", "IntegrationWaitingOld", oldWaits, groups.Min(x => x.OldestWaitingAt));
        Add("info", "IntegrationEvidencePending", groups.Sum(x => x.UnprocessedReceiptCount), groups.Min(x => x.OldestUnprocessedReceiptAt));
        Add("warning", "IntegrationEvidenceOld", groups.Sum(x => x.OldUnprocessedReceiptCount), groups.Min(x => x.OldestUnprocessedReceiptAt));
        foreach (var (state, level, code) in new[] { ("uncertain", "warning", "IntegrationResultUncertain"),
            ("conflict", "error", "IntegrationConflict"), ("failed", "error", "IntegrationFailed"),
            ("diagnostic-required", "warning", "IntegrationMetadataInconsistent") })
            Add(level, code, groups.Where(x => x.State == state).Sum(x => x.Count),
                state == "uncertain" ? groups.Min(x => x.OldestUncertainAt) : null);
        return new(id, now, "not-assessed", OldAttemptMinutes, OldWaitingMinutes, groups.Sum(x => x.Count), groups, alerts);
    }

    public async Task<IntegrationSupervisionPage> ListAsync(int offset, int limit, string? type, string? status,
        string? state, CancellationToken token)
    {
        var id = await workspace.GetCurrentWorkspaceIdAsync(token); var now = clock.GetUtcNow();
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, token);
        await db.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY", token);
        var query = Query(id, now);
        if (type is not null) query = query.Where(x => x.Type == type);
        if (status is not null) query = query.Where(x => x.StatusCode == status);
        if (state is not null) query = query.Where(x => x.State == state);
        var total = await query.LongCountAsync(token);
        var items = await query.OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id).Skip(offset).Take(limit).ToListAsync(token);
        await tx.CommitAsync(token);
        return new(id, now, offset, limit, total, items);
    }
}
