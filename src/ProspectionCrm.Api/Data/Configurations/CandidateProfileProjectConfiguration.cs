using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ProspectionCrm.Api.Entities;

namespace ProspectionCrm.Api.Data.Configurations;

public class CandidateProfileProjectConfiguration : IEntityTypeConfiguration<CandidateProfileProject>
{
    public void Configure(EntityTypeBuilder<CandidateProfileProject> builder)
    {
        builder.HasKey(x => x.Id);
        builder.HasOne(x => x.CandidateProfile).WithMany(x => x.Projects)
            .HasForeignKey(x => x.CandidateProfileId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(x => x.Project).WithMany()
            .HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => new { x.CandidateProfileId, x.ProjectId }).IsUnique()
            .HasDatabaseName("UX_ProfileProjects_Profile_Target");
        builder.HasIndex(x => new { x.CandidateProfileId, x.SortOrder });
        builder.ToTable("CandidateProfileProjects", table =>
        {
            table.HasCheckConstraint("CK_CandidateProfileProjects_SortOrder", "\"SortOrder\" >= 0");
        });
    }
}
