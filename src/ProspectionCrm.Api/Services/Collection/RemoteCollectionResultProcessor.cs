using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ProspectionCrm.Api.Data;
using ProspectionCrm.Api.Dtos.Ingestions;
using ProspectionCrm.Api.Dtos.Integrations;
using ProspectionCrm.Api.Entities;
using ProspectionCrm.Api.Services.Integrations;

namespace ProspectionCrm.Api.Services.Collection;

public sealed class RemoteCollectionResultProcessor(ProspectionCrmDbContext db,
    ICurrentWorkspaceProvider workspaceProvider, RemoteCollectionState remote,
    IIngestionService ingestion, IOptionsMonitor<IntegrationOptions> options)
{
    public async Task<SourceCollectionJobResult<bool>> ProcessAsync(Guid id, CancellationToken token)
    {
        var workspace = await workspaceProvider.GetCurrentWorkspaceIdAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        // Same first lock as all ingestion/identity writers. Then job -> intent -> operation
        // -> receipt -> attempt -> workspace/search/source/pipeline/stage SHARE locks.
        // Reception/cancellation never acquire the identity lock, and keep job before operation.
        await WorkspaceIdentityLock.AcquireAsync(db, workspace, token);
        var job = await db.SourceCollectionJobs.FromSqlInterpolated($"""
            SELECT * FROM "SourceCollectionJobs" WHERE "Id" = {id} AND "WorkspaceId" = {workspace} FOR UPDATE
            """).AsNoTracking().SingleOrDefaultAsync(token);
        if (job is null) return new(SourceCollectionJobStatus.NotFound, Code: "ResourceNotFound");
        if (job.StatusCode != "result-received") return Conflict("RemoteResultNotReady");
        var intent = await db.RemoteCollectionIntents.FromSqlInterpolated($"""
            SELECT * FROM "RemoteCollectionIntents" WHERE "JobId" = {id} AND "WorkspaceId" = {workspace} FOR SHARE
            """).AsNoTracking().SingleOrDefaultAsync(token);
        if (intent is null) return Conflict("RemoteResultBindingMismatch");
        var operation = await db.IntegrationOperations.FromSqlInterpolated($"""
            SELECT * FROM "IntegrationOperations" WHERE "Id" = {intent.OperationId} AND "WorkspaceId" = {workspace} FOR UPDATE
            """).AsNoTracking().SingleOrDefaultAsync(token);
        if (operation is null || operation.ConnectionId != intent.ConnectionId
            || operation.Type != CollectionIntegrationContracts.Type || intent.OperationType != operation.Type)
            return Conflict("RemoteResultBindingMismatch");
        var receipt = await db.IntegrationReceipts.FromSqlInterpolated($"""
            SELECT * FROM "IntegrationReceipts" WHERE "OperationId" = {operation.Id} FOR UPDATE
            """).AsNoTracking().SingleOrDefaultAsync(token);
        var attempt = await db.SourceCollectionJobAttempts.FromSqlInterpolated($"""
            SELECT * FROM "SourceCollectionJobAttempts" WHERE "JobId" = {id} AND "AttemptNumber" = {job.AttemptCount} FOR UPDATE
            """).AsNoTracking().SingleOrDefaultAsync(token);
        if (operation.StatusCode != "result-received" || operation.CompletedAt is not null || receipt is null
            || receipt.ProcessedAt is not null || attempt is null || attempt.WorkspaceId != workspace
            || attempt.StatusCode != "result-received" || attempt.StartedAt != job.StartedAt
            || attempt.FinishedAt is not null || attempt.SourceExecutionId is not null
            || job.StartedAt is null || job.SourceExecutionId is not null || job.LeaseToken is not null)
            return Conflict("RemoteResultBindingMismatch");

        CollectionResultEnvelope result;
        try
        {
            result = CollectionIntegrationContracts.ParseResult(receipt.CanonicalContent);
            var command = CollectionIntegrationContracts.ParseCommand(intent.CommandJson);
            if (receipt.ContentSha256 != Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(receipt.CanonicalContent)))
                || result.MessageId != receipt.MessageId || result.OperationId != operation.Id
                || result.Version != operation.Version || result.Type != operation.Type
                || result.CorrelationId != operation.CorrelationId || result.CommandMessageId != operation.CommandMessageId
                || command.OperationId != operation.Id || command.MessageId != operation.CommandMessageId
                || command.CorrelationId != operation.CorrelationId || command.Version != operation.Version
                || command.Type != operation.Type || result.Payload.RequestId != command.Payload.RequestId
                || result.Payload.Items.Count > command.Payload.MaxItems)
                return await RejectAsync("RemoteResultInvalid");
        }
        catch (IntegrationContractException) { return await RejectAsync("RemoteResultInvalid"); }

        // Disabling transport does not discard a durable result. Changing its logical
        // connection does prevent processing under another installation/configuration.
        var config = options.CurrentValue;
        if (config.WorkspaceId != workspace || config.ConnectionId != intent.ConnectionId)
            return await RejectAsync("RemoteConnectionMismatch");
        try
        {
            var context = await remote.ContextAsync(job, token);
            if (context.Source.Id != intent.SourceConfigurationId
                || intent.Fingerprint != CollectionFingerprint.Create(context.Search, context.Source, context.Pipeline, context.Stage))
                return await RejectAsync("RemoteContextChanged");
        }
        catch (IntegrationConflictException) { return await RejectAsync("RemoteContextChanged"); }

        if (result.Payload.Outcome == "failed")
        {
            await FinishAsync(job, operation, receipt, null, "RemoteCollectionFailed", token);
            await transaction.CommitAsync(token);
            return new(SourceCollectionJobStatus.Succeeded, true);
        }
        var request = new IngestionRequest
        {
            PipelineStageId = job.PipelineStageId,
            Items = result.Payload.Items.Select(x => new IngestionItemRequest
            {
                ExternalId = x.ExternalId, Title = x.Title, SourceUrl = x.SourceUrl,
                Location = x.Location, Description = x.Description
            }).ToList()
        };
        var precondition = new RemoteCollectionPrecondition(workspace, intent.Fingerprint, job.StartedAt.Value,
            job.Id, job.AttemptCount, operation.Id, receipt.MessageId, Guid.NewGuid(), job.TriggerTypeCode);
        var ingested = await ingestion.IngestAsync(job.SavedSearchId, request, precondition, token);
        token.ThrowIfCancellationRequested();
        var executionId = ingested.Value?.Execution.Id ?? ingested.Error?.ExecutionId;
        if (ingested.Status == IngestionStatus.Succeeded && executionId != precondition.ExecutionId)
            throw new InvalidOperationException("Remote ingestion did not return its execution.");
        var code = ingested.Status == IngestionStatus.Succeeded ? null : ingested.Error?.Code switch
        {
            IngestionErrorCode.AmbiguousIdentity => "AmbiguousIdentity",
            IngestionErrorCode.ConcurrentIdentityChange => "ConcurrentIdentityChange",
            IngestionErrorCode.MissingPersistentIdentity => "MissingPersistentIdentity",
            IngestionErrorCode.InvalidBatch => "RemoteResultInvalid",
            _ => "RemoteContextChanged"
        };
        await FinishAsync(job, operation, receipt, executionId, code, token);
        await transaction.CommitAsync(token);
        return code is null ? new(SourceCollectionJobStatus.Succeeded, true) : Conflict(code);

        async Task<SourceCollectionJobResult<bool>> RejectAsync(string code)
        {
            await FinishAsync(job, operation, receipt, null, code, token);
            await transaction.CommitAsync(token);
            return Conflict(code);
        }
    }

    private async Task FinishAsync(SourceCollectionJob job, IntegrationOperation operation, IntegrationReceipt receipt,
        Guid? executionId, string? code, CancellationToken token)
    {
        var status = code is null ? "succeeded" : "failed";
        var now = await db.Database.SqlQuery<DateTimeOffset>($"""
            SELECT GREATEST(statement_timestamp(), {job.StartedAt}, {operation.CreatedAt}, {receipt.ReceivedAt},
                (SELECT "FinishedAt" FROM "SourceExecutions" WHERE "Id" = {executionId})) AS "Value"
            """).SingleAsync(token);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "SourceCollectionJobAttempts" SET "StatusCode" = {status}, "FinishedAt" = {now},
                "SourceExecutionId" = {executionId}, "ErrorCode" = {code}
                WHERE "JobId" = {job.Id} AND "AttemptNumber" = {job.AttemptCount};
            UPDATE "SourceCollectionJobs" SET "StatusCode" = {status}, "FinishedAt" = {now},
                "SourceExecutionId" = {executionId}, "ErrorCode" = {code}, "LeaseToken" = NULL, "LeaseExpiresAt" = NULL
                WHERE "Id" = {job.Id};
            UPDATE "IntegrationReceipts" SET "ProcessedAt" = {now} WHERE "MessageId" = {receipt.MessageId};
            UPDATE "IntegrationOperations" SET "StatusCode" = {status}, "CompletedAt" = {now}, "Revision" = "Revision" + 1
                WHERE "Id" = {operation.Id};
            """, token);
    }

    private static SourceCollectionJobResult<bool> Conflict(string code) => new(SourceCollectionJobStatus.Conflict, Code: code);
}
