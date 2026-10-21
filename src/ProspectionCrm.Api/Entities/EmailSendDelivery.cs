namespace ProspectionCrm.Api.Entities;

// Technical execution of an immutable approval. Sent means provider acceptance, not delivery/read.
public sealed class EmailSendDelivery
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid WorkspaceId { get; set; }
    public Guid EmailSendPlanId { get; set; }
    public Guid AutomationActionRequestId { get; set; }
    public Guid EmailAccountId { get; set; }
    public Guid ConnectionId { get; set; }
    public Guid IntegrationOperationId { get; set; }
    public string OperationType { get; set; } = "email.send";
    public required string ContentHash { get; set; }
    public required string IdempotencyKey { get; set; }
    public required string ProviderCode { get; set; }
    public string StatusCode { get; set; } = "pending";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public string? ExternalMessageId { get; set; }
    public Guid? EmailMessageId { get; set; }
    public string? DiagnosticCode { get; set; }
}
