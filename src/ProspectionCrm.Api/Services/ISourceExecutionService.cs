using ProspectionCrm.Api.Dtos.SourceExecutions;

namespace ProspectionCrm.Api.Services;

public interface ISourceExecutionService
{
    Task<IReadOnlyList<SourceExecutionDto>> GetAllAsync(Guid? sourceConfigurationId = null,
        Guid? savedSearchId = null, string? statusCode = null, CancellationToken cancellationToken = default);
    Task<SourceExecutionDto?> GetByIdAsync(Guid id, CancellationToken cancellationToken);
}
