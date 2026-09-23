using ProspectionCrm.Api.Dtos.Opportunities;

namespace ProspectionCrm.Api.Services;

public interface IOpportunityService
{
    Task<IReadOnlyList<OpportunityDto>> GetAllAsync(bool includeArchived = false, CancellationToken cancellationToken = default);
    Task<OpportunityDto?> GetByIdAsync(Guid id, CancellationToken cancellationToken);
    Task<(OpportunityDto? Opportunity, string? Error)> CreateAsync(
        CreateOpportunityRequest request, CancellationToken cancellationToken);
    Task<(bool Found, string? Error)> UpdateAsync(
        Guid id, UpdateOpportunityRequest request, CancellationToken cancellationToken);
    Task<bool> ArchiveAsync(Guid id, CancellationToken cancellationToken);
    Task<bool> RestoreAsync(Guid id, CancellationToken cancellationToken);
    Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken);
}
