namespace ProspectionCrm.Api.Dtos.IngestionHistory;

public sealed record OpportunitySourceObservationsPageDto(int Offset, int Limit, int TotalCount, bool HasMore,
    IReadOnlyList<SourceExecutionItemDto> Items, Guid OpportunityId, Guid OpportunitySourceId);
