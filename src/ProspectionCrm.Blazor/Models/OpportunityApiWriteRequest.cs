namespace ProspectionCrm.Blazor.Models;

public class OpportunityApiWriteRequest
{
    public string Title { get; init; } = string.Empty;
    public Guid? CompanyId { get; init; }
    public Guid? ContactId { get; init; }
    public Guid? PipelineStageId { get; init; }
    public string PriorityCode { get; init; } = string.Empty;
    public string? Location { get; init; }
    public string? Notes { get; init; }

    public static OpportunityApiWriteRequest FromForm(
        OpportunityApiFormModel model, OpportunityApiDto? original = null) => new()
    {
        Title = model.Title,
        PipelineStageId = model.PipelineStageId,
        PriorityCode = model.PriorityCode,
        Location = model.Location,
        CompanyId = original?.CompanyId,
        ContactId = original?.ContactId,
        Notes = original?.Notes
    };
}
