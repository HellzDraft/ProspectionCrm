using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ProspectionCrm.Api.Entities;

namespace ProspectionCrm.Api.Data.Configurations;

public class CandidateProfileConfiguration : IEntityTypeConfiguration<CandidateProfile>
{
    public void Configure(EntityTypeBuilder<CandidateProfile> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Name).IsRequired().HasMaxLength(200);
        builder.Property(x => x.Description).HasMaxLength(5000);
        builder.HasOne(x => x.Workspace).WithMany(x => x.CandidateProfiles)
            .HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.PrimaryCvDocument).WithMany()
            .HasForeignKey(x => x.PrimaryCvDocumentId).OnDelete(DeleteBehavior.SetNull);
        builder.HasIndex(x => x.PrimaryCvDocumentId);
        builder.HasIndex(x => x.WorkspaceId).IsUnique()
            .HasFilter("\"IsDefault\" = true AND \"ArchivedAt\" IS NULL");
        builder.HasIndex(x => new { x.WorkspaceId, x.ArchivedAt });
        builder.HasIndex(x => new { x.WorkspaceId, x.Name });
    }
}
