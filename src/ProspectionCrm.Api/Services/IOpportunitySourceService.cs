using ProspectionCrm.Api.Dtos.OpportunitySources;

namespace ProspectionCrm.Api.Services;

public interface IOpportunitySourceService
{
    Task<IReadOnlyList<OpportunitySourceDto>?> GetAllAsync(Guid opportunityId, CancellationToken cancellationToken);
    Task<OpportunitySourceDto?> GetByIdAsync(Guid opportunityId, Guid id, CancellationToken cancellationToken);
    Task<(bool Found, OpportunitySourceDto? Source, string? Error)> CreateAsync(Guid opportunityId,
        CreateOpportunitySourceRequest request, CancellationToken cancellationToken);
    Task<(bool Found, string? Error)> UpdateAsync(Guid opportunityId, Guid id,
        UpdateOpportunitySourceRequest request, CancellationToken cancellationToken);
    Task<bool> DeleteAsync(Guid opportunityId, Guid id, CancellationToken cancellationToken);
}
