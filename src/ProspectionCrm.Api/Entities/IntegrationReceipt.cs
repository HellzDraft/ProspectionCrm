namespace ProspectionCrm.Api.Entities;

public sealed class IntegrationReceipt
{
    // Globally unique within this database's n8n result stream (across workspaces).
    public Guid MessageId { get; set; }
    public Guid OperationId { get; set; }
    public int EvidenceNumber { get; set; }
    public required string CanonicalContent { get; set; }
    public required string ContentSha256 { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
    public DateTimeOffset? ProcessedAt { get; set; }
}
