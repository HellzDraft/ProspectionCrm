namespace ProspectionCrm.Api.Dtos.Proposals;

public class ProposalDto
{
    public Guid Id { get; set; }
    public Guid OpportunityId { get; set; }
    public string StatusCode { get; set; } = string.Empty;
    public decimal? Amount { get; set; }
    public string? CurrencyCode { get; set; }
    public string? RateTypeCode { get; set; }
    public DateTimeOffset? SentAt { get; set; }
    public DateTimeOffset? ValidUntil { get; set; }
    public string? Notes { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public Guid? DocumentId { get; set; }
}
