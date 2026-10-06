namespace ProspectionCrm.Api.Dtos.IngestionHistory;

public sealed record OpportunityObservationsPageDto(int Offset, int Limit, int TotalCount, bool HasMore,
    IReadOnlyList<SourceExecutionItemDto> Items, Guid OpportunityId);
