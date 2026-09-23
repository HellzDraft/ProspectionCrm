using System.ComponentModel.DataAnnotations;

namespace ProspectionCrm.Api.Dtos.Proposals;

public class CreateProposalRequest
{
    [Required]
    [MaxLength(50)]
    public string StatusCode { get; set; } = string.Empty;

    public decimal? Amount { get; set; }

    [MaxLength(3)]
    public string? CurrencyCode { get; set; }

    [MaxLength(50)]
    public string? RateTypeCode { get; set; }

    public DateTimeOffset? SentAt { get; set; }

    public DateTimeOffset? ValidUntil { get; set; }

    [MaxLength(10000)]
    public string? Notes { get; set; }
}
