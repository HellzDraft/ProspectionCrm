using Microsoft.EntityFrameworkCore;
using ProspectionCrm.Api.Data;
using ProspectionCrm.Api.Entities;

namespace ProspectionCrm.Api.Services.Collection;

// Caller holds the job row lock until commit. Used by both normal completion and recovery.
public sealed class SourceCollectionJobFinalizer(ProspectionCrmDbContext db, SourceCollectionRetryPolicy policy,
    ILogger<SourceCollectionJobFinalizer> logger)
{
    public async Task FinishAsync(SourceCollectionJob job, string? errorCode, bool recovering, CancellationToken token)
    {
        if (db.Database.CurrentTransaction is null) throw new InvalidOperationException("Finalization requires a transaction.");
        var attempt = await db.SourceCollectionJobAttempts.SingleOrDefaultAsync(x => x.JobId == job.Id && x.AttemptNumber == job.AttemptCount, token);
        var execution = job.SourceExecutionId is null ? null : await db.SourceExecutions.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == job.SourceExecutionId && x.WorkspaceId == job.WorkspaceId, token);
        var ambiguous = attempt is null || attempt.StatusCode != "running"
            || attempt.SourceExecutionId is not null && attempt.SourceExecutionId != job.SourceExecutionId
            || job.SourceExecutionId is not null && (execution is null || execution.SavedSearchId != job.SavedSearchId
                || execution.StatusCode is not ("succeeded" or "failed" or "cancelled"));
        var success = !ambiguous && execution?.StatusCode == "succeeded";
        var code = ambiguous ? "CollectionRecoveryAmbiguous" : SourceCollectionRetryPolicy.Normalize(
            recovering ? execution is null ? attempt?.ErrorCode ?? "CollectionAbandoned" : execution.ErrorMessage ?? attempt?.ErrorCode
            : errorCode ?? execution?.ErrorMessage ?? "CollectionWorkerError");
        if (ambiguous) logger.LogError("Ambiguous collection state for {JobId}; terminating without retry", job.Id);
        var retry = !success && !ambiguous && policy.CanRetry(job.AttemptCount, code, attempt?.UpstreamStatusCode);
        var now = await db.Database.SqlQuery<DateTimeOffset>($"SELECT statement_timestamp() AS \"Value\"").SingleAsync(token);
        if (attempt is null)
        {
            attempt = new() { JobId = job.Id, WorkspaceId = job.WorkspaceId, AttemptNumber = job.AttemptCount, StartedAt = job.StartedAt!.Value };
            db.SourceCollectionJobAttempts.Add(attempt);
        }
        attempt.FinishedAt = now > attempt.StartedAt ? now : attempt.StartedAt;
        attempt.StatusCode = success ? "succeeded" : "failed";
        attempt.ErrorCode = success ? null : code;
        // Never overwrite an inconsistent historical association; retain it for diagnosis.
        if (!ambiguous) attempt.SourceExecutionId = job.SourceExecutionId;
        await db.SaveChangesAsync(token);
        var status = success ? "succeeded" : retry ? "queued" : "failed";
        string? terminalError = success || retry ? null : code;
        var delay = policy.Backoff(job.AttemptCount);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "SourceCollectionJobs" SET "StatusCode" = {status}, "ErrorCode" = {terminalError},
                "AvailableAt" = CASE WHEN {retry} THEN GREATEST(statement_timestamp(), "AvailableAt") + {delay} ELSE "AvailableAt" END,
                "StartedAt" = CASE WHEN {retry} THEN NULL ELSE "StartedAt" END,
                "FinishedAt" = CASE WHEN {retry} THEN NULL ELSE GREATEST(statement_timestamp(), "StartedAt") END,
                "SourceExecutionId" = CASE WHEN {retry} THEN NULL ELSE "SourceExecutionId" END,
                "LeaseToken" = NULL, "LeaseExpiresAt" = NULL
            WHERE "Id" = {job.Id} AND "WorkspaceId" = {job.WorkspaceId} AND "StatusCode" = 'running' AND "LeaseToken" = {job.LeaseToken}
            """, token);
        logger.LogInformation("Collection job {JobId} attempt {AttemptCount} completed as {StatusCode}: {ErrorCode}",
            job.Id, job.AttemptCount, status, success ? null : code);
    }
}
