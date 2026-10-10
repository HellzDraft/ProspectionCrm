namespace ProspectionCrm.Api.Entities;

// Append-only audit marker. No API updates or deletes an existing reset.
public sealed class AutomationCircuitReset
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid WorkspaceId { get; set; }
    public long ResetAfterOutcomeSequence { get; set; }
    public DateTimeOffset RequestedAt { get; set; }
    public Guid RequestedByUserId { get; set; }
    public string? Note { get; set; }
    public Workspace Workspace { get; set; } = null!;
    public UserAccount RequestedByUser { get; set; } = null!;
}
