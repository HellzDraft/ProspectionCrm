namespace ProspectionCrm.Api.Dtos.CandidateProfiles;

public class CandidateProfileDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public Guid? PrimaryCvDocumentId { get; set; }
    public bool IsDefault { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public DateTimeOffset? ArchivedAt { get; set; }
    public IReadOnlyList<CandidateProfileExperienceDto> Experiences { get; set; } = [];
    public IReadOnlyList<CandidateProfileEducationDto> Educations { get; set; } = [];
    public IReadOnlyList<CandidateProfileProjectDto> Projects { get; set; } = [];
    public IReadOnlyList<CandidateProfileSkillDto> Skills { get; set; } = [];
}
