using Microsoft.EntityFrameworkCore;
using ProspectionCrm.Api.Data;
using ProspectionCrm.Api.Dtos.Collection;
using ProspectionCrm.Api.Dtos.Ingestions;
using ProspectionCrm.Api.Entities;

namespace ProspectionCrm.Api.Services.Collection;

public interface ISourceCollectionService
{
    Task<SourceCollectionResult> CollectAsync(Guid savedSearchId, CollectSavedSearchRequest request, CancellationToken token);
}
public sealed record SourceCollectionResult(int StatusCode, SourceCollectionDto? Value = null,
    SourceAdapterError? Error = null, Guid? ExecutionId = null, int? ItemIndex = null);

public sealed class SourceCollectionService(ProspectionCrmDbContext db, ICurrentWorkspaceProvider workspaceProvider,
    SourceAdapterRegistry adapters, IIngestionService ingestion, IServiceScopeFactory scopes,
    ILogger<SourceCollectionService> logger) : ISourceCollectionService
{
    public async Task<SourceCollectionResult> CollectAsync(Guid savedSearchId, CollectSavedSearchRequest request, CancellationToken token)
    {
        try { return await CollectCoreAsync(savedSearchId, request, token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            // Preflight database/registry failures must also have a controlled HTTP contract,
            // including in Development where an uncaught exception could expose diagnostic details.
            logger.LogWarning("Collection preflight failed for {SavedSearchId}: {Code}", savedSearchId, "CollectionInternalError");
            return Fail("CollectionInternalError", 500);
        }
    }

    private async Task<SourceCollectionResult> CollectCoreAsync(Guid savedSearchId, CollectSavedSearchRequest request, CancellationToken token)
    {
        if (request.PipelineStageId is null) return Fail("InvalidRequest", 400);
        Guid workspaceId;
        try { workspaceId = await workspaceProvider.GetCurrentWorkspaceIdAsync(token); }
        catch (InvalidOperationException) { return Fail("WorkspaceUnavailable", 409); }
        var search = await db.SavedSearches.AsNoTracking().SingleOrDefaultAsync(x => x.Id == savedSearchId && x.WorkspaceId == workspaceId, token);
        if (search is null) return Fail("ResourceNotFound", 404);
        var source = await db.SourceConfigurations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == search.SourceConfigurationId && x.WorkspaceId == workspaceId, token);
        var pipeline = await db.Pipelines.AsNoTracking().SingleOrDefaultAsync(x => x.Id == search.PipelineId && x.WorkspaceId == workspaceId, token);
        var stage = await db.PipelineStages.AsNoTracking().SingleOrDefaultAsync(x => x.Id == request.PipelineStageId && x.Pipeline.WorkspaceId == workspaceId, token);
        if (source is null || pipeline is null || stage is null) return Fail("ResourceNotFound", 404);
        if (!search.Enabled || search.ArchivedAt is not null || !source.Enabled || source.ArchivedAt is not null
            || pipeline.ArchivedAt is not null || stage.ArchivedAt is not null) return Fail("InactiveResource", 409);
        if (stage.PipelineId != pipeline.Id) return Fail("WrongPipeline", 409);
        var adapter = adapters.Find(source.SourceTypeCode);
        if (adapter is null) return Fail("UnsupportedSourceType", 409);
        var context = new SourceAdapterContext(search.SearchUrl, search.CriteriaJson);
        var invalid = adapter.Validate(context);
        if (invalid is not null) return new(invalid.StatusCode, Error: invalid);
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
        catch (Exception) { return await FailedAttemptAsync(new("CollectionInternalError", 500)); }

        IngestionResult result;
        try
        {
            result = await ingestion.IngestAsync(search.Id, new IngestionRequest
            {
                PipelineStageId = stage.Id, Items = collected.Items.ToList()
            }, new CollectionPrecondition(workspaceId, fingerprint, started), token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; } // ingestion owns its cancellation history
        catch (Exception) { return await FailedAttemptAsync(new("CollectionInternalError", 500)); }
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
            var executionId = await PreserveAsync(error.Code, "failed");
            return new(error.StatusCode, Error: error, ExecutionId: executionId);
        }
        async Task<Guid?> PreserveAsync(string code, string status)
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                // Fresh scope: do not flush or reuse any failed business change tracker/transaction.
                await using var scope = scopes.CreateAsyncScope();
                var historyDb = scope.ServiceProvider.GetRequiredService<ProspectionCrmDbContext>();
                var execution = new SourceExecution
                {
                    WorkspaceId = workspaceId, SourceConfigurationId = source.Id, SavedSearchId = search.Id,
                    TriggerTypeCode = "manual", StatusCode = status, StartedAt = started, FinishedAt = DateTimeOffset.UtcNow,
                    ErrorMessage = code, HistoryVersion = 1, ContractVersion = 1, NormalizationVersion = 1,
                    TargetPipelineId = pipeline.Id, TargetPipelineStageId = stage.Id, ContextSnapshotJson = snapshot
                };
                historyDb.SourceExecutions.Add(execution);
                await historyDb.SaveChangesAsync(cleanup.Token);
                logger.LogWarning("Collection ended for {WorkspaceId} {SavedSearchId} {SourceConfigurationId}: {Code}, {ExecutionId}",
                    workspaceId, search.Id, source.Id, code, execution.Id);
                return execution.Id;
            }
            catch (Exception)
            {
                // The original controlled error wins, including when the original FK targets were deleted.
                logger.LogWarning("Collection history unavailable for {WorkspaceId} {SavedSearchId} {SourceConfigurationId}: {Code}",
                    workspaceId, search.Id, source.Id, code);
                return null;
            }
        }
    }
    private static SourceCollectionResult Fail(string code, int status) => new(status, Error: new(code, status));
}
