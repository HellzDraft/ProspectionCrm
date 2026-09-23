namespace ProspectionCrm.Api.Entities;

public class EmailMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid WorkspaceId { get; set; }
    public Guid? OpportunityId { get; set; }
    public Guid? CompanyId { get; set; }
    public Guid? ContactId { get; set; }
    public required string ProviderCode { get; set; }
    public string? ExternalMessageId { get; set; }
    public string? ExternalThreadId { get; set; }
    public required string DirectionCode { get; set; }
    public required string FromAddress { get; set; }
    public required string ToAddressesJson { get; set; }
    public string? CcAddressesJson { get; set; }
    public string? Subject { get; set; }
    public string? BodyText { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public Workspace Workspace { get; set; } = null!;
    public Opportunity? Opportunity { get; set; }
    public Company? Company { get; set; }
    public Contact? Contact { get; set; }
}
