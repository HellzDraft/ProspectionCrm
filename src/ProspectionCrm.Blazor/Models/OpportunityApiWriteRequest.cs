namespace ProspectionCrm.Blazor.Models;

public class OpportunityApiWriteRequest
{
    public string Title { get; init; } = string.Empty;
    public Guid? CompanyId { get; init; }
    public Guid? ContactId { get; init; }
    public string PipelineCode { get; init; } = string.Empty;
    public string StatusCode { get; init; } = string.Empty;
    public string PriorityCode { get; init; } = string.Empty;
    public string? Location { get; init; }
    public string? SourceName { get; init; }
    public string? SourceUrl { get; init; }
    public string? Notes { get; init; }
    public DateTimeOffset? FollowUpDueAt { get; init; }

    public static OpportunityApiWriteRequest FromForm(
        OpportunityApiFormModel model, OpportunityApiDto? original = null) => new()
    {
        Title = model.Title,
        PipelineCode = model.PipelineCode,
        StatusCode = model.StatusCode,
        PriorityCode = model.PriorityCode,
        Location = model.Location,
        CompanyId = original?.CompanyId,
        ContactId = original?.ContactId,
        SourceName = original?.SourceName,
        SourceUrl = original?.SourceUrl,
        Notes = original?.Notes,
        FollowUpDueAt = original?.FollowUpDueAt
    };
}
