namespace ProspectionCrm.Api.Dtos.CandidateProfiles;

public class CandidateProfileExperienceDto
{
    public Guid Id { get; set; }
    public Guid ExperienceId { get; set; }
    public int SortOrder { get; set; }
}
