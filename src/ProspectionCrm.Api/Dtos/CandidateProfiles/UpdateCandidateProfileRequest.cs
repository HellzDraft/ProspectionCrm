using System.ComponentModel.DataAnnotations;

namespace ProspectionCrm.Api.Dtos.CandidateProfiles;

public class UpdateCandidateProfileRequest
{
    [Required]
    [MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(5000)]
    public string? Description { get; set; }

    public Guid? PrimaryCvDocumentId { get; set; }

    public bool IsDefault { get; set; }
}
