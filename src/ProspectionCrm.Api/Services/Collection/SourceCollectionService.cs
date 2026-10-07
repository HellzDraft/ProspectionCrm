using ProspectionCrm.Api.Dtos.Collection;

namespace ProspectionCrm.Api.Services.Collection;

public interface ISourceCollectionService
{
    Task<SourceCollectionResult> CollectAsync(Guid savedSearchId, CollectSavedSearchRequest request, CancellationToken token);
}
public sealed record SourceCollectionResult(int StatusCode, SourceCollectionDto? Value = null,
    SourceAdapterError? Error = null, Guid? ExecutionId = null, int? ItemIndex = null);

public sealed class SourceCollectionService(ICurrentWorkspaceProvider workspaceProvider,
    ISourceCollectionOrchestrator orchestrator, ILogger<SourceCollectionService> logger) : ISourceCollectionService
{
    public async Task<SourceCollectionResult> CollectAsync(Guid savedSearchId, CollectSavedSearchRequest request, CancellationToken token)
    {
        if (request.PipelineStageId is null) return new(400, Error: new("InvalidRequest", 400));
        try
        {
            Guid workspaceId;
            try { workspaceId = await workspaceProvider.GetCurrentWorkspaceIdAsync(token); }
            catch (InvalidOperationException) { return new(409, Error: new("WorkspaceUnavailable", 409)); }
            return await orchestrator.CollectAsync(workspaceId, savedSearchId, request.PipelineStageId.Value, null, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            logger.LogError(exception, "Collection preflight failed for {SavedSearchId}", savedSearchId);
            return new(500, Error: new("CollectionInternalError", 500));
        }
    }
}
