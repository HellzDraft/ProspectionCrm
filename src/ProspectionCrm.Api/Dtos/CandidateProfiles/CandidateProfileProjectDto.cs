namespace ProspectionCrm.Api.Dtos.CandidateProfiles;

public class CandidateProfileProjectDto
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public int SortOrder { get; set; }
}
