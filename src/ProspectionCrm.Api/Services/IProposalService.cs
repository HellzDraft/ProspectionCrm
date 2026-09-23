using ProspectionCrm.Api.Dtos.Proposals;

namespace ProspectionCrm.Api.Services;

public interface IProposalService
{
    Task<IReadOnlyList<ProposalDto>?> GetAllAsync(Guid opportunityId, CancellationToken cancellationToken);
    Task<ProposalDto?> GetByIdAsync(Guid opportunityId, Guid id, CancellationToken cancellationToken);
    Task<(bool Found, ProposalDto? Proposal, string? Error)> CreateAsync(Guid opportunityId,
        CreateProposalRequest request, CancellationToken cancellationToken);
    Task<(bool Found, string? Error)> UpdateAsync(Guid opportunityId, Guid id,
        UpdateProposalRequest request, CancellationToken cancellationToken);
    Task<bool> DeleteAsync(Guid opportunityId, Guid id, CancellationToken cancellationToken);
}
