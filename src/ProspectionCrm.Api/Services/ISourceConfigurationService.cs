using ProspectionCrm.Api.Dtos.SourceConfigurations;

namespace ProspectionCrm.Api.Services;

public interface ISourceConfigurationService
{
    Task<IReadOnlyList<SourceConfigurationDto>> GetAllAsync(bool includeArchived = false, CancellationToken cancellationToken = default);
    Task<SourceConfigurationDto?> GetByIdAsync(Guid id, CancellationToken cancellationToken);
    Task<(SourceConfigurationDto? SourceConfiguration, string? Error)> CreateAsync(CreateSourceConfigurationRequest request, CancellationToken cancellationToken);
    Task<(bool Found, string? Error)> UpdateAsync(Guid id, UpdateSourceConfigurationRequest request, CancellationToken cancellationToken);
    Task<bool> ArchiveAsync(Guid id, CancellationToken cancellationToken);
    Task<(bool Found, string? Error)> RestoreAsync(Guid id, CancellationToken cancellationToken);
}
