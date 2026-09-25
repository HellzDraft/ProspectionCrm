using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ProspectionCrm.Api.Entities;

namespace ProspectionCrm.Api.Data.Configurations;

public class PipelineConfiguration : IEntityTypeConfiguration<Pipeline>
{
    public void Configure(EntityTypeBuilder<Pipeline> builder)
    {
        builder.HasKey(x => x.Id);
        builder.HasOne(x => x.PreferredCandidateProfile).WithMany()
            .HasForeignKey(x => x.PreferredCandidateProfileId).OnDelete(DeleteBehavior.SetNull);
        builder.HasIndex(x => x.PreferredCandidateProfileId);
        builder.Property(x => x.Name).IsRequired().HasMaxLength(200);
        builder.Property(x => x.Description).HasMaxLength(2000);
        builder.Property(x => x.TypeCode).IsRequired().HasMaxLength(50);
        builder.HasOne(x => x.Workspace).WithMany(x => x.Pipelines)
            .HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => new { x.WorkspaceId, x.ArchivedAt });
        builder.ToTable("Pipelines", table => table.HasCheckConstraint(
            "CK_Pipelines_TypeCode", "\"TypeCode\" IN ('employment', 'freelance', 'business', 'custom')"));
    }
}
