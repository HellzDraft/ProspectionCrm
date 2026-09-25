namespace ProspectionCrm.Api.Entities;

public class CandidateProfileSkill
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CandidateProfileId { get; set; }
    public Guid SkillId { get; set; }
    public int SortOrder { get; set; }

    public CandidateProfile CandidateProfile { get; set; } = null!;
    public Skill Skill { get; set; } = null!;
}
