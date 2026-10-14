namespace ProspectionCrm.Api.Entities;

// Context belongs exclusively to the CRM; it is never supplied by a result envelope.
public sealed class IntegrationOperation
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid? ConnectionId { get; set; }
    public Guid CommandMessageId { get; set; }
    public Guid CorrelationId { get; set; }
    public Guid ProbeId { get; set; }
    public int Version { get; set; }
    public required string Type { get; set; }
    public required string StatusCode { get; set; }
    public long Revision { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}
