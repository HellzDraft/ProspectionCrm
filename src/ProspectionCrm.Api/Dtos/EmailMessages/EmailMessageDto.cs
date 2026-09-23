namespace ProspectionCrm.Api.Dtos.EmailMessages;

public class EmailMessageDto
{
    public Guid Id { get; set; }
    public Guid? OpportunityId { get; set; }
    public Guid? CompanyId { get; set; }
    public Guid? ContactId { get; set; }
    public string ProviderCode { get; set; } = string.Empty;
    public string? ExternalMessageId { get; set; }
    public string? ExternalThreadId { get; set; }
    public string DirectionCode { get; set; } = string.Empty;
    public string FromAddress { get; set; } = string.Empty;
    public string ToAddressesJson { get; set; } = string.Empty;
    public string? CcAddressesJson { get; set; }
    public string? Subject { get; set; }
    public string? BodyText { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
