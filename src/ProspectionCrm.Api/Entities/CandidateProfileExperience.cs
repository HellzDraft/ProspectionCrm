namespace ProspectionCrm.Api.Entities;

public class CandidateProfileExperience
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CandidateProfileId { get; set; }
    public Guid ExperienceId { get; set; }
    public int SortOrder { get; set; }

    public CandidateProfile CandidateProfile { get; set; } = null!;
    public Experience Experience { get; set; } = null!;
}
