namespace ProspectionCrm.Api.Entities;

public class CandidateProfileProject
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CandidateProfileId { get; set; }
    public Guid ProjectId { get; set; }
    public int SortOrder { get; set; }

    public CandidateProfile CandidateProfile { get; set; } = null!;
    public Project Project { get; set; } = null!;
}
