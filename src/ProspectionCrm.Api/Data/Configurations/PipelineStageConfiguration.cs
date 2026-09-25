using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ProspectionCrm.Api.Entities;

namespace ProspectionCrm.Api.Data.Configurations;

public class PipelineStageConfiguration : IEntityTypeConfiguration<PipelineStage>
{
    public void Configure(EntityTypeBuilder<PipelineStage> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Name).IsRequired().HasMaxLength(200);
        builder.Property(x => x.Description).HasMaxLength(2000);
        builder.Property(x => x.CategoryCode).IsRequired().HasMaxLength(50);
        builder.HasOne(x => x.Pipeline).WithMany(x => x.Stages)
            .HasForeignKey(x => x.PipelineId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => new { x.PipelineId, x.SortOrder }).IsUnique()
            .HasDatabaseName("UX_PipelineStages_Pipeline_SortOrder");
        builder.ToTable("PipelineStages", table =>
        {
            table.HasCheckConstraint("CK_PipelineStages_SortOrder", "\"SortOrder\" >= 0");
            table.HasCheckConstraint("CK_PipelineStages_CategoryCode", "\"CategoryCode\" IN ('active', 'success', 'failure')");
        });
    }
}
