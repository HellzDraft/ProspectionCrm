using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ProspectionCrm.Api.Data;
using ProspectionCrm.Api.Entities;

namespace ProspectionCrm.Api.Services.Collection;

public interface ISourceCollectionJobQueue
{
    Task<SourceCollectionJob?> ClaimAsync(CancellationToken token);
    Task<bool> CompleteAsync(Guid jobId, Guid workspaceId, Guid leaseToken, string? errorCode, CancellationToken token);
}

public sealed class SourceCollectionJobQueue(ProspectionCrmDbContext db, IOptions<SourceCollectionWorkerOptions> options,
    SourceCollectionJobFinalizer? finalizer = null)
    : ISourceCollectionJobQueue
{
    public async Task<SourceCollectionJob?> ClaimAsync(CancellationToken token)
    {
        var leaseToken = Guid.NewGuid();
        var duration = TimeSpan.FromSeconds(options.Value.LeaseDurationSeconds);
        // One PostgreSQL statement/transaction. RETURNING is the actual updated row, not the candidate.
        // PostgreSQL's clock is authoritative. No claim transaction or EF connection spans the fetch.
        var rows = await db.SourceCollectionJobs.FromSqlInterpolated($"""
            WITH candidate AS (
                SELECT "Id" FROM "SourceCollectionJobs"
                WHERE "StatusCode" = 'queued' AND "AvailableAt" <= statement_timestamp() AND "AttemptCount" < {options.Value.MaxAttempts}
                ORDER BY "AvailableAt", "EnqueuedAt", "Id"
                LIMIT 1 FOR UPDATE SKIP LOCKED
            )
            , owned AS (
                SELECT "Id" FROM candidate
                WHERE pg_try_advisory_xact_lock(hashtextextended({SourceCollectionJobGuard.Prefix} || "Id"::text, 0))
            ), claimed AS (UPDATE "SourceCollectionJobs" j
            SET "StatusCode" = 'running', "StartedAt" = statement_timestamp(),
                "AttemptCount" = j."AttemptCount" + 1,
                "LeaseToken" = {leaseToken}, "LeaseExpiresAt" = statement_timestamp() + {duration}
            FROM owned c WHERE j."Id" = c."Id"
            RETURNING j.*), attempts AS (
                INSERT INTO "SourceCollectionJobAttempts" ("JobId", "WorkspaceId", "AttemptNumber", "StartedAt", "StatusCode")
                SELECT "Id", "WorkspaceId", "AttemptCount", "StartedAt", 'running' FROM claimed
                RETURNING "JobId"
            )
            SELECT claimed.* FROM claimed JOIN attempts ON attempts."JobId" = claimed."Id"
            """).AsNoTracking().ToListAsync(token);
        return rows.SingleOrDefault();
    }

    public async Task<bool> CompleteAsync(Guid jobId, Guid workspaceId, Guid leaseToken, string? errorCode, CancellationToken token)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var rows = await db.SourceCollectionJobs.FromSqlInterpolated($"""
            SELECT * FROM "SourceCollectionJobs" WHERE "Id" = {jobId} AND "WorkspaceId" = {workspaceId}
                AND "StatusCode" = 'running' AND "LeaseToken" = {leaseToken} FOR UPDATE
            """).AsNoTracking().ToListAsync(token);
        var job = rows.SingleOrDefault();
        if (job is null) return false;
        var completion = finalizer ?? new SourceCollectionJobFinalizer(db, new SourceCollectionRetryPolicy(options),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<SourceCollectionJobFinalizer>.Instance);
        await completion.FinishAsync(job, errorCode, recovering: false, token);
        await transaction.CommitAsync(token);
        return true;
    }
}
