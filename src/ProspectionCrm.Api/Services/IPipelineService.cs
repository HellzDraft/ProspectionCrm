using ProspectionCrm.Api.Dtos.Pipelines;

namespace ProspectionCrm.Api.Services;

public interface IPipelineService
{
    Task<IReadOnlyList<PipelineDto>> GetAllAsync(bool includeArchived = false, CancellationToken cancellationToken = default);
    Task<PipelineDto?> GetByIdAsync(Guid id, bool includeArchivedStages = false, CancellationToken cancellationToken = default);
    Task<(PipelineDto? Pipeline, string? Error)> CreateAsync(PipelineWriteRequest request, CancellationToken cancellationToken);
    Task<(bool Found, string? Error)> UpdateAsync(Guid id, PipelineWriteRequest request, CancellationToken cancellationToken);
    Task<bool> ArchiveAsync(Guid id, CancellationToken cancellationToken);
    Task<bool> RestoreAsync(Guid id, CancellationToken cancellationToken);
    Task<(bool Found, string? Error)> SetDefaultAsync(Guid id, CancellationToken cancellationToken);
}
