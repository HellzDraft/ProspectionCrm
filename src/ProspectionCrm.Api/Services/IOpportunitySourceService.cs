using ProspectionCrm.Api.Dtos.OpportunitySources;

namespace ProspectionCrm.Api.Services;

public interface IOpportunitySourceService
{
    Task<IReadOnlyList<OpportunitySourceDto>?> GetAllAsync(Guid opportunityId, CancellationToken cancellationToken);
    Task<OpportunitySourceDto?> GetByIdAsync(Guid opportunityId, Guid id, CancellationToken cancellationToken);
    Task<OpportunitySourceWriteResult> CreateAsync(Guid opportunityId,
        CreateOpportunitySourceRequest request, CancellationToken cancellationToken);
    Task<OpportunitySourceWriteResult> UpdateAsync(Guid opportunityId, Guid id,
        UpdateOpportunitySourceRequest request, CancellationToken cancellationToken);
    Task<OpportunitySourceWriteResult> DeleteAsync(Guid opportunityId, Guid id, CancellationToken cancellationToken);
}

public enum OpportunitySourceWriteStatus { Succeeded, NotFound, InvalidInput, Conflict }
public enum OpportunitySourceErrorCode
{
    InvalidSourceUrl, MissingSourceIdentity, InvalidReference, InvalidInput, InvalidTimestamps,
    DuplicateExternalId, DuplicateSourceUrl, ImmutableSourceIdentity, SourceIdentityInUse, ConcurrentIdentityChange
}
public sealed record OpportunitySourceWriteResult(OpportunitySourceWriteStatus Status,
    OpportunitySourceDto? Source = null, OpportunitySourceErrorCode? Code = null, string? Detail = null);
