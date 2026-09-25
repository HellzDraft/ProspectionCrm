using System.ComponentModel.DataAnnotations;

namespace ProspectionCrm.Api.Dtos.Applications;

public class UpdateApplicationRequest
{
    [Required]
    [MaxLength(50)]
    public string StatusCode { get; set; } = string.Empty;

    public DateTimeOffset? SubmittedAt { get; set; }

    [MaxLength(50)]
    public string? ChannelCode { get; set; }

    [MaxLength(10000)]
    public string? Notes { get; set; }
    public Guid? CandidateProfileId { get; set; }
    public Guid? CvDocumentId { get; set; }
    public Guid? CoverLetterDocumentId { get; set; }
}
