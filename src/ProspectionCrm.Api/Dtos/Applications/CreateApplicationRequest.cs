using System.ComponentModel.DataAnnotations;

namespace ProspectionCrm.Api.Dtos.Applications;

public class CreateApplicationRequest
{
    [Required]
    [MaxLength(50)]
    public string StatusCode { get; set; } = string.Empty;

    public DateTimeOffset? SubmittedAt { get; set; }

    [MaxLength(50)]
    public string? ChannelCode { get; set; }

    [MaxLength(10000)]
    public string? Notes { get; set; }
}
