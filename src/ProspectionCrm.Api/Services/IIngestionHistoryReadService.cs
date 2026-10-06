using ProspectionCrm.Api.Dtos.IngestionHistory;

namespace ProspectionCrm.Api.Services;

public enum IngestionHistoryReadStatus { Succeeded, NotFound, InvalidRequest }
public enum IngestionHistoryReadErrorCode
{
    InvalidPagination, InvalidOutcomeCode, InvalidDecisionCode, InvalidRoleCode, InvalidDateRange, InvalidReference, HistoryResourceNotFound
}
public sealed record IngestionHistoryReadResult<T>(IngestionHistoryReadStatus Status, T? Value = default,
    IngestionHistoryReadErrorCode? Code = null, string? Detail = null);

public interface IIngestionHistoryReadService
{
    Task<IngestionHistoryReadResult<SourceExecutionHistoryDto>> GetExecutionHistoryAsync(Guid id, CancellationToken cancellationToken);
    Task<IngestionHistoryReadResult<SourceExecutionItemsPageDto>> GetExecutionItemsAsync(Guid id, ExecutionItemsQuery query, CancellationToken cancellationToken);
    Task<IngestionHistoryReadResult<OpportunityObservationsPageDto>> GetOpportunityObservationsAsync(Guid id, OpportunityObservationsQuery query, CancellationToken cancellationToken);
    Task<IngestionHistoryReadResult<OpportunitySourceObservationsPageDto>> GetSourceObservationsAsync(Guid opportunityId, Guid sourceId, OpportunitySourceObservationsQuery query, CancellationToken cancellationToken);
}
