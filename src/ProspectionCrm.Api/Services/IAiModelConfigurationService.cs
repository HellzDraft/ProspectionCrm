using ProspectionCrm.Api.Dtos.AiModelConfigurations;

namespace ProspectionCrm.Api.Services;

public interface IAiModelConfigurationService
{
    Task<IReadOnlyList<AiModelConfigurationDto>> GetAllAsync(bool includeArchived = false, CancellationToken cancellationToken = default);
    Task<AiModelConfigurationDto?> GetByIdAsync(Guid id, CancellationToken cancellationToken);
    Task<(AiModelConfigurationDto? Item, string? Error)> CreateAsync(CreateAiModelConfigurationRequest request, CancellationToken cancellationToken);
    Task<(bool Found, string? Error)> UpdateAsync(Guid id, UpdateAiModelConfigurationRequest request, CancellationToken cancellationToken);
    Task<bool> ArchiveAsync(Guid id, CancellationToken cancellationToken);
    Task<(bool Found, string? Error)> RestoreAsync(Guid id, CancellationToken cancellationToken);
}
