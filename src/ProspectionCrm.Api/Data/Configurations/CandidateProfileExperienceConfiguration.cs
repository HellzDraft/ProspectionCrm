using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ProspectionCrm.Api.Entities;

namespace ProspectionCrm.Api.Data.Configurations;

public class CandidateProfileExperienceConfiguration : IEntityTypeConfiguration<CandidateProfileExperience>
{
    public void Configure(EntityTypeBuilder<CandidateProfileExperience> builder)
    {
        builder.HasKey(x => x.Id);
        builder.HasOne(x => x.CandidateProfile).WithMany(x => x.Experiences)
            .HasForeignKey(x => x.CandidateProfileId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(x => x.Experience).WithMany()
            .HasForeignKey(x => x.ExperienceId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => new { x.CandidateProfileId, x.ExperienceId }).IsUnique()
            .HasDatabaseName("UX_ProfileExperiences_Profile_Target");
        builder.HasIndex(x => new { x.CandidateProfileId, x.SortOrder });
        builder.ToTable("CandidateProfileExperiences", table =>
        {
            table.HasCheckConstraint("CK_CandidateProfileExperiences_SortOrder", "\"SortOrder\" >= 0");
        });
    }
}
