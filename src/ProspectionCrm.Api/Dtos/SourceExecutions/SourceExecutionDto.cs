namespace ProspectionCrm.Api.Dtos.SourceExecutions;

public class SourceExecutionDto
{
    public Guid Id { get; set; }
    public Guid SourceConfigurationId { get; set; }
    public Guid? SavedSearchId { get; set; }
    public string TriggerTypeCode { get; set; } = string.Empty;
    public string StatusCode { get; set; } = string.Empty;
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public int ItemsFound { get; set; }
    public int ItemsCreated { get; set; }
    public int ItemsUpdated { get; set; }
    public int ItemsIgnored { get; set; }
    public string? ErrorMessage { get; set; }
}
