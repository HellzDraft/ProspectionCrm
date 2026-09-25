using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ProspectionCrm.Api.Entities;

namespace ProspectionCrm.Api.Data.Configurations;

public class CandidateProfileEducationConfiguration : IEntityTypeConfiguration<CandidateProfileEducation>
{
    public void Configure(EntityTypeBuilder<CandidateProfileEducation> builder)
    {
        builder.HasKey(x => x.Id);
        builder.HasOne(x => x.CandidateProfile).WithMany(x => x.Educations)
            .HasForeignKey(x => x.CandidateProfileId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(x => x.Education).WithMany()
            .HasForeignKey(x => x.EducationId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => new { x.CandidateProfileId, x.EducationId }).IsUnique()
            .HasDatabaseName("UX_ProfileEducations_Profile_Target");
        builder.HasIndex(x => new { x.CandidateProfileId, x.SortOrder });
        builder.ToTable("CandidateProfileEducations", table =>
        {
            table.HasCheckConstraint("CK_CandidateProfileEducations_SortOrder", "\"SortOrder\" >= 0");
        });
    }
}
