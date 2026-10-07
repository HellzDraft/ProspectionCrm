using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ProspectionCrm.Api.Data;

namespace ProspectionCrm.Api.Services.Collection;

public interface ISourceCollectionJobRecovery
{
    Task<int> RecoverAsync(CancellationToken token);
}

public sealed class SourceCollectionJobRecovery(ProspectionCrmDbContext db, SourceCollectionJobFinalizer finalizer,
    IOptions<SourceCollectionWorkerOptions> options)
    : ISourceCollectionJobRecovery
{
    public async Task<int> RecoverAsync(CancellationToken token)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        // A configuration change can lower MaxAttempts while a job is already in backoff.
        // Terminate it when available, without claiming or inventing another attempt.
        var count = await db.Database.ExecuteSqlInterpolatedAsync($"""
            WITH exhausted AS (
                SELECT "Id" FROM "SourceCollectionJobs" WHERE "StatusCode" = 'queued'
                    AND "AvailableAt" <= statement_timestamp() AND "AttemptCount" >= {options.Value.MaxAttempts}
                ORDER BY "AvailableAt", "EnqueuedAt", "Id" LIMIT 8 FOR UPDATE SKIP LOCKED
            )
            UPDATE "SourceCollectionJobs" j SET "StatusCode" = 'failed', "ErrorCode" = 'CollectionAttemptsExhausted',
                "StartedAt" = statement_timestamp(), "FinishedAt" = statement_timestamp()
            FROM exhausted e WHERE j."Id" = e."Id"
            """, token);
        var jobs = await db.SourceCollectionJobs.FromSqlRaw("""
            SELECT * FROM "SourceCollectionJobs"
            WHERE "StatusCode" = 'running' AND "LeaseExpiresAt" <= statement_timestamp()
            ORDER BY "LeaseExpiresAt", "Id" LIMIT 8 FOR UPDATE SKIP LOCKED
            """).AsNoTracking().ToListAsync(token);
        foreach (var job in jobs)
        {
            var key = SourceCollectionJobGuard.Key(job.Id);
            // Same lock as the live worker, nonblocking and released at commit/rollback.
            var acquired = await db.Database.SqlQuery<bool>(
                $"SELECT pg_try_advisory_xact_lock(hashtextextended({key}, 0)) AS \"Value\"").SingleAsync(token);
            if (!acquired) continue;
            await finalizer.FinishAsync(job, null, recovering: true, token); count++;
        }
        await transaction.CommitAsync(token); return count;
    }
}
