using ProspectionCrm.Api.Dtos.Ingestions;

namespace ProspectionCrm.Api.Services;

public interface IIngestionService
{
    Task<IngestionResult> IngestAsync(Guid savedSearchId, IngestionRequest request, CancellationToken cancellationToken);
    Task<IngestionResult> IngestAsync(Guid savedSearchId, IngestionRequest request,
        Collection.CollectionPrecondition precondition, CancellationToken cancellationToken);
}

public enum IngestionStatus { Succeeded, InvalidRequest, NotFound, Conflict, Failed }
public enum IngestionErrorCode
{
    InvalidBatch, WorkspaceUnavailable, ResourceNotFound, InactiveResource, WrongPipeline,
    AmbiguousIdentity, MissingPersistentIdentity, ConcurrentIdentityChange, PersistenceFailure, CollectionConfigurationChanged
}
public sealed record IngestionError(IngestionErrorCode Code, string Detail, int? ItemIndex = null, Guid? ExecutionId = null);
public sealed record IngestionResult(IngestionStatus Status, IngestionDto? Value = null, IngestionError? Error = null);
