namespace ProspectionCrm.Api.Dtos.IngestionHistory;

public sealed record SourceExecutionItemSourceDto(Guid Id, Guid? OpportunitySourceId, Guid OpportunitySourceIdSnapshot, string RoleCode);
