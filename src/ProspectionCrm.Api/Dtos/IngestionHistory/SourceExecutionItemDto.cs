using System.Text.Json;

namespace ProspectionCrm.Api.Dtos.IngestionHistory;

public sealed class SourceExecutionItemDto
{
    public Guid Id { get; set; }
    public Guid SourceExecutionId { get; set; }
    public Guid SourceConfigurationId { get; set; }
    public Guid? SavedSearchId { get; set; }
    public string TriggerTypeCode { get; set; } = string.Empty;
    public string ExecutionStatusCode { get; set; } = string.Empty;
    public DateTimeOffset ExecutionStartedAt { get; set; }
    public Guid? TargetPipelineId { get; set; }
    public Guid? TargetPipelineStageId { get; set; }
    public int ItemIndex { get; set; }
    public Guid? OpportunityId { get; set; }
    public Guid? OpportunityIdSnapshot { get; set; }
    public string OutcomeCode { get; set; } = string.Empty;
    public string DecisionCode { get; set; } = string.Empty;
    public DateTimeOffset ReceivedAt { get; set; }
    public DateTimeOffset? ProcessedAt { get; set; }
    public string Title { get; set; } = string.Empty;
    public string NormalizedTitle { get; set; } = string.Empty;
    public string? CompanyName { get; set; }
    public string? NormalizedCompanyName { get; set; }
    public string? ExternalId { get; set; }
    public string? SourceUrl { get; set; }
    public string? NormalizedSourceUrl { get; set; }
    public JsonElement? PayloadSnapshot { get; set; }
    public JsonElement? DecisionDetails { get; set; }
    public IReadOnlyList<SourceExecutionItemSourceDto> Sources { get; set; } = [];
}
