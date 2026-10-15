using Microsoft.EntityFrameworkCore;
using ProspectionCrm.Api.Data;

namespace ProspectionCrm.Api.Services.Collection;

// Internal metadata for an already received result. The caller owns the transaction and job lock.
// No dispatch lease is fabricated; the execution is attached together with terminal job state.
public sealed record RemoteCollectionPrecondition(Guid WorkspaceId, string Fingerprint, DateTimeOffset StartedAt,
    Guid JobId, int AttemptNumber, Guid OperationId, Guid ReceiptId, Guid ExecutionId, string TriggerTypeCode)
    : CollectionPrecondition(WorkspaceId, Fingerprint, StartedAt)
{
    internal async Task ValidateAsync(ProspectionCrmDbContext db, Guid searchId, Guid stageId, CancellationToken token)
    {
        if (db.Database.CurrentTransaction is null || Job is not null || ExecutionId == Guid.Empty)
            throw new InvalidOperationException("Remote ingestion requires its processing transaction.");
        var valid = await db.Database.SqlQuery<bool>($"""
            SELECT EXISTS (
                SELECT 1 FROM "SourceCollectionJobs" j
                JOIN "SourceCollectionJobAttempts" a ON a."JobId" = j."Id" AND a."AttemptNumber" = j."AttemptCount"
                JOIN "RemoteCollectionIntents" i ON i."JobId" = j."Id"
                JOIN "IntegrationOperations" o ON o."Id" = i."OperationId"
                JOIN "IntegrationReceipts" r ON r."OperationId" = o."Id"
                WHERE j."Id" = {JobId} AND j."WorkspaceId" = {WorkspaceId}
                    AND j."SavedSearchId" = {searchId} AND j."PipelineStageId" = {stageId}
                    AND j."StartedAt" = {StartedAt} AND j."TriggerTypeCode" = {TriggerTypeCode}
                    AND j."AttemptCount" = {AttemptNumber} AND j."StatusCode" = 'result-received'
                    AND j."SourceExecutionId" IS NULL AND j."LeaseToken" IS NULL
                    AND a."WorkspaceId" = j."WorkspaceId" AND a."StatusCode" = 'result-received'
                    AND a."SourceExecutionId" IS NULL AND a."StartedAt" = j."StartedAt"
                    AND i."WorkspaceId" = j."WorkspaceId" AND i."Fingerprint" = {Fingerprint}
                    AND o."Id" = {OperationId} AND o."WorkspaceId" = i."WorkspaceId"
                    AND o."ConnectionId" = i."ConnectionId" AND o."Type" = i."OperationType"
                    AND o."Type" = 'collection.request' AND o."StatusCode" = 'result-received'
                    AND r."MessageId" = {ReceiptId} AND r."ProcessedAt" IS NULL
            ) AS "Value"
            """).SingleAsync(token);
        if (!valid) throw new InvalidOperationException("Remote ingestion binding is no longer valid.");
    }
}
