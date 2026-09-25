namespace ProspectionCrm.Api.Entities;

public class CandidateProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid WorkspaceId { get; set; }
    public required string Name { get; set; }
    public string? Description { get; set; }
    public Guid? PrimaryCvDocumentId { get; set; }
    public bool IsDefault { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UpdatedAt { get; set; }
    public DateTimeOffset? ArchivedAt { get; set; }

    public Workspace Workspace { get; set; } = null!;
    public Document? PrimaryCvDocument { get; set; }
    public ICollection<CandidateProfileExperience> Experiences { get; set; } = new List<CandidateProfileExperience>();
    public ICollection<CandidateProfileEducation> Educations { get; set; } = new List<CandidateProfileEducation>();
    public ICollection<CandidateProfileProject> Projects { get; set; } = new List<CandidateProfileProject>();
    public ICollection<CandidateProfileSkill> Skills { get; set; } = new List<CandidateProfileSkill>();
}
