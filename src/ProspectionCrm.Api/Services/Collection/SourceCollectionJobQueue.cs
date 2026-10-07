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

public sealed class SourceCollectionJobQueue(ProspectionCrmDbContext db, IOptions<SourceCollectionWorkerOptions> options)
    : ISourceCollectionJobQueue
{
    public async Task<SourceCollectionJob?> ClaimAsync(CancellationToken token)
    {
        var leaseToken = Guid.NewGuid();
        var duration = TimeSpan.FromSeconds(options.Value.LeaseDurationSeconds);
        // One PostgreSQL statement/transaction. RETURNING is the actual updated row, not the candidate.
        // PostgreSQL's clock is authoritative. No transaction or connection spans the source fetch.
        var rows = await db.SourceCollectionJobs.FromSqlInterpolated($"""
            WITH candidate AS (
                SELECT "Id" FROM "SourceCollectionJobs"
                WHERE "StatusCode" = 'queued' AND "AvailableAt" <= statement_timestamp()
                ORDER BY "AvailableAt", "EnqueuedAt", "Id"
                LIMIT 1 FOR UPDATE SKIP LOCKED
            )
            UPDATE "SourceCollectionJobs" j
            SET "StatusCode" = 'running', "StartedAt" = statement_timestamp(),
                "AttemptCount" = j."AttemptCount" + 1,
                "LeaseToken" = {leaseToken}, "LeaseExpiresAt" = statement_timestamp() + {duration}
            FROM candidate c WHERE j."Id" = c."Id"
            RETURNING j.*
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
        var execution = job.SourceExecutionId is null ? null : await db.SourceExecutions.AsNoTracking()
            .SingleAsync(x => x.Id == job.SourceExecutionId && x.WorkspaceId == workspaceId, token);
        // A committed success wins over cancellation/connection errors observed just after commit.
        var succeeded = execution?.StatusCode == "succeeded";
        var status = succeeded ? "succeeded" : "failed";
        var code = succeeded ? null : errorCode ?? "CollectionWorkerError";
        // Only the same owner may finish, including cooperative cleanup after expiration.
        // Expired running jobs are NEVER automatically reclaimed in Phase 7.2.
        var count = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "SourceCollectionJobs" SET "StatusCode" = {status},
                "FinishedAt" = GREATEST(statement_timestamp(), "StartedAt"), "ErrorCode" = {code},
                "LeaseToken" = NULL, "LeaseExpiresAt" = NULL
            WHERE "Id" = {jobId} AND "WorkspaceId" = {workspaceId}
                AND "StatusCode" = 'running' AND "LeaseToken" = {leaseToken}
            """, token);
        await transaction.CommitAsync(token);
        return count == 1;
    }
}
