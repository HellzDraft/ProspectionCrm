using ProspectionCrm.Api.Dtos.Campaigns;

namespace ProspectionCrm.Api.Services;

public interface ICampaignService
{
    Task<IReadOnlyList<CampaignDto>> GetAllAsync(bool includeArchived = false, CancellationToken cancellationToken = default);
    Task<CampaignDto?> GetByIdAsync(Guid id, CancellationToken cancellationToken);
    Task<(CampaignDto? Campaign, string? Error)> CreateAsync(CreateCampaignRequest request, CancellationToken cancellationToken);
    Task<(bool Found, string? Error)> UpdateAsync(Guid id, UpdateCampaignRequest request, CancellationToken cancellationToken);
    Task<bool> ArchiveAsync(Guid id, CancellationToken cancellationToken);
    Task<(bool Found, string? Error)> RestoreAsync(Guid id, CancellationToken cancellationToken);
    Task<(bool Found, string? Error)> AddOpportunityAsync(Guid campaignId, Guid opportunityId, CancellationToken cancellationToken);
    Task<bool> RemoveOpportunityAsync(Guid campaignId, Guid opportunityId, CancellationToken cancellationToken);
}
