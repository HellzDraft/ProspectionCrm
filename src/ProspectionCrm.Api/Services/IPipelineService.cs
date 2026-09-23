using ProspectionCrm.Api.Dtos.Pipelines;

namespace ProspectionCrm.Api.Services;

public interface IPipelineService
{
    Task<IReadOnlyList<PipelineDto>> GetAllAsync(bool includeArchived = false, CancellationToken cancellationToken = default);
}
