using ProspectionCrm.Api.Dtos.SavedSearches;

namespace ProspectionCrm.Api.Services;

public interface ISavedSearchService
{
    Task<IReadOnlyList<SavedSearchDto>> GetAllAsync(bool includeArchived = false, Guid? pipelineId = null, Guid? sourceConfigurationId = null, bool? enabled = null, CancellationToken cancellationToken = default);
    Task<SavedSearchDto?> GetByIdAsync(Guid id, CancellationToken cancellationToken);
    Task<(SavedSearchDto? SavedSearch, string? Error)> CreateAsync(CreateSavedSearchRequest request, CancellationToken cancellationToken);
    Task<(bool Found, string? Error)> UpdateAsync(Guid id, UpdateSavedSearchRequest request, CancellationToken cancellationToken);
    Task<bool> ArchiveAsync(Guid id, CancellationToken cancellationToken);
    Task<(bool Found, string? Error)> RestoreAsync(Guid id, CancellationToken cancellationToken);
}
