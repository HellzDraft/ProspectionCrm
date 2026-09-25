using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ProspectionCrm.Api.Entities;

namespace ProspectionCrm.Api.Data.Configurations;

public class CandidateProfileSkillConfiguration : IEntityTypeConfiguration<CandidateProfileSkill>
{
    public void Configure(EntityTypeBuilder<CandidateProfileSkill> builder)
    {
        builder.HasKey(x => x.Id);
        builder.HasOne(x => x.CandidateProfile).WithMany(x => x.Skills)
            .HasForeignKey(x => x.CandidateProfileId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(x => x.Skill).WithMany()
            .HasForeignKey(x => x.SkillId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => new { x.CandidateProfileId, x.SkillId }).IsUnique()
            .HasDatabaseName("UX_ProfileSkills_Profile_Target");
        builder.HasIndex(x => new { x.CandidateProfileId, x.SortOrder });
        builder.ToTable("CandidateProfileSkills", table =>
        {
            table.HasCheckConstraint("CK_CandidateProfileSkills_SortOrder", "\"SortOrder\" >= 0");
        });
    }
}
