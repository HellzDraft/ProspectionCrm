using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ProspectionCrm.Api.Entities;

namespace ProspectionCrm.Api.Data.Configurations;

public class WorkspaceConfiguration : IEntityTypeConfiguration<Workspace>
{
    public void Configure(EntityTypeBuilder<Workspace> builder)
    {
        builder.HasKey(x => x.Id);
        // Keep ID generation explicit now that Id also participates in the default-pipeline FK.
        builder.Property(x => x.Id).ValueGeneratedOnAdd();
        builder.Property(x => x.Name).IsRequired().HasMaxLength(200);
        builder.Property(x => x.TimeZoneId).IsRequired().HasMaxLength(100);
        builder.HasOne(x => x.OwnerUser).WithMany(x => x.OwnedWorkspaces)
            .HasForeignKey(x => x.OwnerUserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => x.OwnerUserId);
        builder.HasIndex(x => x.ArchivedAt);
        // A nullable default, restricted to a pipeline owned by this workspace; no cascade cycle.
        builder.HasOne(x => x.DefaultPipeline).WithMany()
            .HasForeignKey(x => new { x.Id, x.DefaultPipelineId })
            .HasPrincipalKey(x => new { x.WorkspaceId, x.Id })
            .OnDelete(DeleteBehavior.Restrict);
    }
}
