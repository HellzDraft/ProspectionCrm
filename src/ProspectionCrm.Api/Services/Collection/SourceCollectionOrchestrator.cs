using ProspectionCrm.Api.Data;
using ProspectionCrm.Api.Dtos.Collection;
using ProspectionCrm.Api.Dtos.Ingestions;
using ProspectionCrm.Api.Entities;

namespace ProspectionCrm.Api.Services.Collection;

public interface ISourceCollectionOrchestrator
{
    Task<SourceCollectionResult> CollectAsync(Guid workspaceId, Guid savedSearchId, Guid pipelineStageId,
        CollectionJobAttempt? job, CancellationToken token);
}

public sealed class SourceCollectionOrchestrator(
    ISourceCollectionContextResolver resolver, IIngestionService ingestion, IServiceScopeFactory scopes,
    ILogger<SourceCollectionOrchestrator> logger) : ISourceCollectionOrchestrator
{
    public async Task<SourceCollectionResult> CollectAsync(Guid workspaceId, Guid savedSearchId, Guid pipelineStageId, CollectionJobAttempt? job, CancellationToken token)
    {
        try { return await CollectCoreAsync(workspaceId, savedSearchId, pipelineStageId, job, token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            // Preflight database/registry failures must also have a controlled HTTP contract,
            // including in Development where an uncaught exception could expose diagnostic details.
            logger.LogError(exception, "Collection preflight failed for {SavedSearchId}: {Code}", savedSearchId, "CollectionInternalError");
            return Fail("CollectionInternalError", 500);
        }
    }

    private async Task<SourceCollectionResult> CollectCoreAsync(Guid workspaceId, Guid savedSearchId, Guid pipelineStageId, CollectionJobAttempt? job, CancellationToken token)
    {
        var resolved = await resolver.ResolveAsync(workspaceId, savedSearchId, pipelineStageId, token);
        if (resolved.Error is { } validationError) return new(validationError.StatusCode, Error: validationError);
        var (search, source, pipeline, stage, adapter, context) = resolved.Value!;
        var fingerprint = CollectionFingerprint.Create(search, source, pipeline, stage);
        var snapshot = IngestionHistory.Context(source, search, pipeline, stage);
        // All reads are materialized/no-tracking. EF has closed its connection and there is no transaction.
        token.ThrowIfCancellationRequested();
        var started = DateTimeOffset.UtcNow;
        SourceAdapterResult collected;
        try
        {
            collected = await adapter.CollectAsync(context, token);
            if (collected.Items.Count == 0) throw new SourceCollectionException(new("NoUsableFeedEntries", 422));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            await PreserveAsync("CollectionCancelled", "cancelled");
            throw;
        }
        catch (SourceCollectionException exception) { return await FailedAttemptAsync(exception.Error); }
        catch (Exception exception) { logger.LogError(exception, "Collection attempt failed for {SavedSearchId}", savedSearchId); return await FailedAttemptAsync(new("CollectionInternalError", 500)); }

        IngestionResult result;
        try
        {
            result = await ingestion.IngestAsync(search.Id, new IngestionRequest
            {
                PipelineStageId = stage.Id, Items = collected.Items.ToList()
            }, new CollectionPrecondition(workspaceId, fingerprint, started, job), token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; } // ingestion owns its cancellation history
        catch (Exception exception) { logger.LogError(exception, "Collection attempt failed for {SavedSearchId}", savedSearchId); return await FailedAttemptAsync(new("CollectionInternalError", 500)); }
        if (result.Status != IngestionStatus.Succeeded)
        {
            var status = result.Status switch { IngestionStatus.InvalidRequest => 400, IngestionStatus.NotFound => 404,
                IngestionStatus.Conflict => 409, _ => 500 };
            var error = result.Error!;
            var executionId = error.ExecutionId ?? await PreserveAsync(error.Code.ToString(), "failed");
            return new(status, Error: new(error.Code.ToString(), status), ExecutionId: executionId, ItemIndex: error.ItemIndex);
        }
        var summary = collected.Summary;
        logger.LogInformation("Collection completed for {WorkspaceId} {SavedSearchId} {SourceConfigurationId}: {EntriesMapped} entries, {ExecutionId}",
            workspaceId, search.Id, source.Id, summary.EntriesMapped, result.Value!.Execution.Id);
        return new(201, new(result.Value, new(summary.SourceTypeCode, summary.FeedFormat, summary.EntriesRead,
            summary.EntriesMapped, summary.EntriesSkipped, summary.WasTruncated)));

        async Task<SourceCollectionResult> FailedAttemptAsync(SourceAdapterError error)
        {
            var executionId = await PreserveAsync(error.Code, "failed", error.UpstreamStatusCode);
            return new(error.StatusCode, Error: error, ExecutionId: executionId);
        }
        async Task<Guid?> PreserveAsync(string code, string status, int? upstreamStatus = null)
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                // Fresh scope: do not flush or reuse any failed business change tracker/transaction.
                await using var scope = scopes.CreateAsyncScope();
                var historyDb = scope.ServiceProvider.GetRequiredService<ProspectionCrmDbContext>();
                var execution = new SourceExecution
                {
                    Id = job?.ExecutionId ?? Guid.NewGuid(),
                    WorkspaceId = workspaceId, SourceConfigurationId = source.Id, SavedSearchId = search.Id,
                    TriggerTypeCode = job?.TriggerTypeCode ?? "manual", StatusCode = status, StartedAt = started, FinishedAt = DateTimeOffset.UtcNow,
                    ErrorMessage = code, HistoryVersion = 1, ContractVersion = 1, NormalizationVersion = 1,
                    TargetPipelineId = pipeline.Id, TargetPipelineStageId = stage.Id, ContextSnapshotJson = snapshot
                };
                await using var historyTransaction = await historyDb.Database.BeginTransactionAsync(cleanup.Token);
                historyDb.SourceExecutions.Add(execution);
                await historyDb.SaveChangesAsync(cleanup.Token);
                if (job is not null) await job.AttachExecutionAsync(historyDb, workspaceId, cleanup.Token, upstreamStatus, code);
                await historyTransaction.CommitAsync(cleanup.Token);
                logger.LogWarning("Collection ended for {WorkspaceId} {SavedSearchId} {SourceConfigurationId}: {Code}, {ExecutionId}",
                    workspaceId, search.Id, source.Id, code, execution.Id);
                return execution.Id;
            }
            catch (Exception exception)
            {
                // The original controlled error wins, including when the original FK targets were deleted.
                logger.LogError(exception, "Collection history unavailable for {WorkspaceId} {SavedSearchId} {SourceConfigurationId}: {Code}",
                    workspaceId, search.Id, source.Id, code);
                return null;
            }
        }
    }
    private static SourceCollectionResult Fail(string code, int status) => new(status, Error: new(code, status));
}
