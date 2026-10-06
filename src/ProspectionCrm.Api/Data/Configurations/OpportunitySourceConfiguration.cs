using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ProspectionCrm.Api.Entities;

namespace ProspectionCrm.Api.Data.Configurations;

public class OpportunitySourceConfiguration : IEntityTypeConfiguration<OpportunitySource>
{
    public void Configure(EntityTypeBuilder<OpportunitySource> builder)
    {
        builder.HasKey(x => x.Id);
        builder.HasAlternateKey(x => new { x.WorkspaceId, x.Id });
        builder.Property(x => x.NormalizedSourceUrl).HasMaxLength(2048);
        builder.HasOne(x => x.Workspace).WithMany(x => x.OpportunitySources)
            .HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
        builder.Property(x => x.SourceLabel).IsRequired().HasMaxLength(200);
        builder.Property(x => x.SourceUrl).HasMaxLength(2048);
        builder.Property(x => x.ExternalId).HasMaxLength(500);
        builder.HasOne(x => x.Opportunity).WithMany(x => x.OpportunitySources)
            .HasForeignKey(x => new { x.WorkspaceId, x.OpportunityId })
            .HasPrincipalKey(x => new { x.WorkspaceId, x.Id }).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(x => x.SourceConfiguration).WithMany(x => x.OpportunitySources)
            .HasForeignKey(x => new { x.WorkspaceId, x.SourceConfigurationId })
            .HasPrincipalKey(x => new { x.WorkspaceId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.SavedSearch).WithMany(x => x.OpportunitySources)
            .HasForeignKey(x => new { x.WorkspaceId, x.SavedSearchId })
            .HasPrincipalKey(x => new { x.WorkspaceId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.SourceExecution).WithMany(x => x.OpportunitySources)
            .HasForeignKey(x => new { x.WorkspaceId, x.SourceExecutionId })
            .HasPrincipalKey(x => new { x.WorkspaceId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => new { x.WorkspaceId, x.OpportunityId });
        builder.HasIndex(x => new { x.WorkspaceId, x.SourceConfigurationId });
        builder.HasIndex(x => new { x.WorkspaceId, x.SavedSearchId });
        builder.HasIndex(x => new { x.WorkspaceId, x.SourceExecutionId });
        builder.HasIndex(x => x.SourceUrl);
        builder.HasIndex(x => new { x.WorkspaceId, x.SourceConfigurationId, x.ExternalId }).IsUnique()
            .HasDatabaseName("UX_OpportunitySources_Workspace_SourceConfiguration_ExternalId")
            .HasFilter("\"SourceConfigurationId\" IS NOT NULL AND \"ExternalId\" IS NOT NULL");
        builder.HasIndex(x => new { x.WorkspaceId, x.NormalizedSourceUrl }).IsUnique()
            .HasDatabaseName("UX_OpportunitySources_Workspace_NormalizedSourceUrl")
            .HasFilter("\"NormalizedSourceUrl\" IS NOT NULL");
        builder.ToTable("OpportunitySources", table =>
        {
            table.HasCheckConstraint("CK_OpportunitySources_UrlPair", """
                ("SourceUrl" IS NULL) = ("NormalizedSourceUrl" IS NULL)
                """);
            table.HasCheckConstraint("CK_OpportunitySources_ExternalConfiguration", """
                "ExternalId" IS NULL OR "SourceConfigurationId" IS NOT NULL
                """);
            table.HasCheckConstraint("CK_OpportunitySources_LastSeenAt", "\"LastSeenAt\" IS NULL OR \"LastSeenAt\" >= \"FirstSeenAt\"");
        });
    }
}
