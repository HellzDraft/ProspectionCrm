using ProspectionCrm.Api.Dtos.CollectionJobs;

namespace ProspectionCrm.Api.Services.Collection;

public enum SourceCollectionJobStatus { Succeeded, NotFound, InvalidRequest, Conflict }
public sealed record SourceCollectionJobResult<T>(SourceCollectionJobStatus Status, T? Value = default,
    string? Code = null, Guid? ExistingJobId = null);

public interface ISourceCollectionJobService
{
    Task<SourceCollectionJobResult<SourceCollectionJobDto>> EnqueueAsync(Guid savedSearchId, EnqueueSourceCollectionJobRequest request, CancellationToken token);
    Task<SourceCollectionJobResult<SourceCollectionJobDto>> GetAsync(Guid id, CancellationToken token);
    Task<SourceCollectionJobResult<SourceCollectionJobsPageDto>> ListAsync(int offset, int limit, string? statusCode,
        Guid? savedSearchId, string? triggerTypeCode, CancellationToken token);
    Task<SourceCollectionJobResult<bool>> CancelAsync(Guid id, CancellationToken token);
}
