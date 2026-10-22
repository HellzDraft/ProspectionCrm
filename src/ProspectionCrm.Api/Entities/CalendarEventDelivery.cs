namespace ProspectionCrm.Api.Entities;

// Execution state belongs here; the approved plan remains immutable.
public sealed class CalendarEventDelivery
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid WorkspaceId { get; set; }
    public Guid CalendarEventPlanId { get; set; }
    public Guid AutomationActionRequestId { get; set; }
    public Guid CalendarAccountId { get; set; }
    public Guid CalendarId { get; set; }
    public Guid ConnectionId { get; set; }
    public Guid IntegrationOperationId { get; set; }
    public required string OperationType { get; set; }
    public required string ContentHash { get; set; }
    public required string IdempotencyKey { get; set; }
    public required string ProviderCode { get; set; }
    public required string ExternalCalendarId { get; set; }
    public Guid? TargetCalendarEventId { get; set; }
    public string? TargetExternalEventId { get; set; }
    public long? TargetExternalRevision { get; set; }
    public string? TargetContentHash { get; set; }
    public string StatusCode { get; set; } = "pending";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public string? ExternalEventId { get; set; }
    public long? ExternalRevision { get; set; }
    public Guid? CalendarEventId { get; set; }
    public string? ProofHash { get; set; }
    public string? DiagnosticCode { get; set; }
}
