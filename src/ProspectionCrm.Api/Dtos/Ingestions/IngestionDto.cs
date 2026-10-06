using ProspectionCrm.Api.Dtos.SourceExecutions;

namespace ProspectionCrm.Api.Dtos.Ingestions;

public sealed record IngestionDto(SourceExecutionDto Execution, IReadOnlyList<IngestionItemDto> Items);
public sealed record IngestionItemDto(int Index, Guid OpportunityId, string Outcome);
