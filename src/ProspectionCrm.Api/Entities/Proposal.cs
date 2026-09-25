namespace ProspectionCrm.Api.Entities;

public class Proposal
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OpportunityId { get; set; }
    public required string StatusCode { get; set; }
    public decimal? Amount { get; set; }
    public string? CurrencyCode { get; set; }
    public string? RateTypeCode { get; set; }
    public DateTimeOffset? SentAt { get; set; }
    public DateTimeOffset? ValidUntil { get; set; }
    public string? Notes { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UpdatedAt { get; set; }

    public Opportunity Opportunity { get; set; } = null!;
    public Guid? DocumentId { get; set; }
    public Document? Document { get; set; }
}
