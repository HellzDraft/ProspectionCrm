using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ProspectionCrm.Api.Entities;

namespace ProspectionCrm.Api.Data.Configurations;

public class CampaignConfiguration : IEntityTypeConfiguration<Campaign>
{
    public void Configure(EntityTypeBuilder<Campaign> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Name).IsRequired().HasMaxLength(200);
        builder.Property(x => x.Description).HasMaxLength(2000);
        builder.Property(x => x.StatusCode).IsRequired().HasMaxLength(50);
        builder.HasOne(x => x.Workspace).WithMany(x => x.Campaigns)
            .HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.Pipeline).WithMany(x => x.Campaigns)
            .HasForeignKey(x => x.PipelineId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => new { x.WorkspaceId, x.ArchivedAt });
        builder.HasIndex(x => new { x.WorkspaceId, x.StatusCode });
        builder.HasIndex(x => x.PipelineId);
        builder.ToTable("Campaigns", table =>
        {
            table.HasCheckConstraint("CK_Campaigns_StatusCode", "\"StatusCode\" IN ('draft', 'active', 'paused', 'completed', 'cancelled')");
            table.HasCheckConstraint("CK_Campaigns_EndsAt", "\"EndsAt\" IS NULL OR \"StartsAt\" IS NULL OR \"EndsAt\" >= \"StartsAt\"");
        });
    }
}
