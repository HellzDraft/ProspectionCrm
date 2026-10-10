using Microsoft.EntityFrameworkCore;
using ProspectionCrm.Api.Data;
using ProspectionCrm.Api.Dtos.AutomationSupervision;
using ProspectionCrm.Api.Entities;

namespace ProspectionCrm.Api.Services.Automation;

// Internal workspace IDs come from runtime jobs or the HTTP workspace provider, never a client body.
public sealed class AutomationCircuitBreakerService(ProspectionCrmDbContext db, TimeProvider clock)
{
    public async Task<AutomationCircuitStatus> ReadAsync(AutomationRuntimeSettings settings, CancellationToken token)
    {
        // One statement snapshot. Count the full suffix, not a threshold-capped count.
        // Sequence, not timestamp, determines the latest reset, success and threshold crossing.
        var row = await db.Database.SqlQuery<CircuitRead>($"""
            WITH reset AS (
                SELECT * FROM "AutomationCircuitResets" WHERE "WorkspaceId" = {settings.WorkspaceId}
                ORDER BY "ResetAfterOutcomeSequence" DESC LIMIT 1
            ), outcomes AS NOT MATERIALIZED (
                SELECT "OutcomeSequence" AS seq, "FinishedAt" AS at, "EffectApplied" AS success
                FROM "AutomationExecutions"
                WHERE "WorkspaceId" = {settings.WorkspaceId} AND "IsAutomaticAttempt"
                    AND "OutcomeSequence" IS NOT NULL AND NOT "IsDeferred"
                    AND ("EffectApplied" OR "StatusCode" = 'failed')
            ), failures AS (
                SELECT * FROM outcomes WHERE seq > GREATEST(
                    COALESCE((SELECT "ResetAfterOutcomeSequence" FROM reset), 0),
                    COALESCE((SELECT max(seq) FROM outcomes WHERE success), 0))
            )
            SELECT
                (SELECT count(*) FROM failures) AS "ConsecutiveFailureCount",
                (SELECT max(seq) FROM outcomes) AS "LastAutomaticOutcomeSequence",
                (SELECT at FROM outcomes ORDER BY seq DESC LIMIT 1) AS "LastAutomaticOutcomeAt",
                (SELECT at FROM outcomes WHERE success ORDER BY seq DESC LIMIT 1) AS "LastAutomaticSuccessAt",
                (SELECT at FROM outcomes WHERE NOT success ORDER BY seq DESC LIMIT 1) AS "LastAutomaticFailureAt",
                (SELECT at FROM failures ORDER BY seq OFFSET {settings.MaxConsecutiveFailures - 1} LIMIT 1) AS "OpenedAt",
                (SELECT "Id" FROM reset) AS "LastResetId",
                (SELECT "RequestedAt" FROM reset) AS "LastResetAt",
                (SELECT "RequestedByUserId" FROM reset) AS "LastResetByUserId",
                (SELECT "Note" FROM reset) AS "LastResetNote",
                COALESCE((SELECT "ResetAfterOutcomeSequence" FROM reset), 0) AS "ResetAfterOutcomeSequence"
            """).SingleAsync(token);
        return new(settings.WorkspaceId, settings.MaxConsecutiveFailures, row.ConsecutiveFailureCount,
            row.LastAutomaticOutcomeSequence, row.LastAutomaticOutcomeAt, row.LastAutomaticSuccessAt,
            row.LastAutomaticFailureAt, row.OpenedAt, row.LastResetId, row.LastResetAt,
            row.LastResetByUserId, row.LastResetNote, row.ResetAfterOutcomeSequence);
    }

    public async Task<AutomationJobResult<AutomationCircuitResetResult>> ResetAsync(Guid workspace,
        ResetAutomationCircuitRequest request, CancellationToken token)
    {
        if (request.Note is { } note && (note.Length > 2000 || !AutomationJson.SafeText(note)))
            return new(null, 400, "InvalidResetNote");
        await using var tx = await db.Database.BeginTransactionAsync(token);
        await AutomationRuntimeLock.AcquireAsync(db, workspace, token);
        var settings = await db.AutomationRuntimeSettings.FromSqlInterpolated($"""
            SELECT * FROM "AutomationRuntimeSettings" WHERE "WorkspaceId" = {workspace} FOR SHARE
            """).AsNoTracking().SingleOrDefaultAsync(token);
        if (settings is null) return new(null, 409, "AutomationSettingsMissing");
        var before = await ReadAsync(settings, token);
        if (!before.CanReset)
        {
            await tx.CommitAsync(token);
            return new(new(before, false, null));
        }
        var owner = await db.Workspaces.FromSqlInterpolated($"""
            SELECT * FROM "Workspaces" WHERE "Id" = {workspace} FOR SHARE
            """).AsNoTracking().SingleOrDefaultAsync(token);
        if (owner is null) return new(null, 409, "WorkspaceUnavailable");
        var marker = new AutomationCircuitReset { WorkspaceId = workspace,
            ResetAfterOutcomeSequence = before.LastAutomaticOutcomeSequence!.Value,
            RequestedByUserId = owner.OwnerUserId, RequestedAt = clock.GetUtcNow().ToUniversalTime(),
            Note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note };
        // The shared workspace lock serializes every outcome and reset. The unique index also
        // protects the invariant against duplicate inserts from another database writer.
        db.AutomationCircuitResets.Add(marker);
        await db.SaveChangesAsync(token);
        var after = await ReadAsync(settings, token);
        await tx.CommitAsync(token);
        return new(new(after, true, ToDto(marker)));
    }

    public async Task<AutomationJobResult<AutomationCircuitResetsPage>> ListAsync(Guid workspace, int offset,
        int limit, CancellationToken token)
    {
        if (offset < 0 || limit is < 1 or > 200) return new(null, 400, "InvalidPagination");
        var query = db.AutomationCircuitResets.AsNoTracking().Where(x => x.WorkspaceId == workspace);
        var count = await query.CountAsync(token);
        var items = await query.OrderByDescending(x => x.RequestedAt).ThenByDescending(x => x.Id)
            .Skip(offset).Take(limit).Select(x => new AutomationCircuitResetDto(x.Id,
                x.ResetAfterOutcomeSequence, x.RequestedAt, x.RequestedByUserId, x.Note)).ToListAsync(token);
        return new(new(offset, limit, count, (long)offset + items.Count < count, items));
    }

    private static AutomationCircuitResetDto ToDto(AutomationCircuitReset r) =>
        new(r.Id, r.ResetAfterOutcomeSequence, r.RequestedAt, r.RequestedByUserId, r.Note);

    private sealed record CircuitRead(long ConsecutiveFailureCount, long? LastAutomaticOutcomeSequence,
        DateTimeOffset? LastAutomaticOutcomeAt, DateTimeOffset? LastAutomaticSuccessAt,
        DateTimeOffset? LastAutomaticFailureAt, DateTimeOffset? OpenedAt, Guid? LastResetId,
        DateTimeOffset? LastResetAt, Guid? LastResetByUserId, string? LastResetNote, long ResetAfterOutcomeSequence);
}
