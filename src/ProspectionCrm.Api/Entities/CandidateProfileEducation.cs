namespace ProspectionCrm.Api.Entities;

public class CandidateProfileEducation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CandidateProfileId { get; set; }
    public Guid EducationId { get; set; }
    public int SortOrder { get; set; }

    public CandidateProfile CandidateProfile { get; set; } = null!;
    public Education Education { get; set; } = null!;
}
