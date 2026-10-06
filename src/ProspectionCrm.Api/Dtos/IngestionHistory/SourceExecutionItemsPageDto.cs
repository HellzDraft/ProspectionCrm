namespace ProspectionCrm.Api.Dtos.IngestionHistory;

public sealed record SourceExecutionItemsPageDto(int Offset, int Limit, int TotalCount, bool HasMore,
    IReadOnlyList<SourceExecutionItemDto> Items, Guid SourceExecutionId, bool HistoryAvailable);
