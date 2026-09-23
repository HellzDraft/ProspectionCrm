namespace ProspectionCrm.Blazor.Models;

public class CrmTaskApiWriteRequest
{
    public Guid? OpportunityId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public DateTimeOffset? DueAt { get; set; }

    public static CrmTaskApiWriteRequest FromForm(CrmTaskApiFormModel model) => new()
    {
        OpportunityId = model.OpportunityId,
        Title = model.Title,
        Description = model.Description,
        // datetime-local has no offset. Interpret it in the browser runtime's local zone.
        DueAt = model.DueAtLocal is { } local
            ? new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Local)).ToUniversalTime()
            : null
    };
}
