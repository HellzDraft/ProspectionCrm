using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ProspectionCrm.Api.Entities;

namespace ProspectionCrm.Api.Data.Configurations;

public class OpportunitySourceConfiguration : IEntityTypeConfiguration<OpportunitySource>
{
    public void Configure(EntityTypeBuilder<OpportunitySource> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.SourceLabel).IsRequired().HasMaxLength(200);
        builder.Property(x => x.SourceUrl).HasMaxLength(2048);
        builder.Property(x => x.ExternalId).HasMaxLength(500);
        builder.HasOne(x => x.Opportunity).WithMany(x => x.OpportunitySources)
            .HasForeignKey(x => x.OpportunityId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(x => x.SourceConfiguration).WithMany(x => x.OpportunitySources)
            .HasForeignKey(x => x.SourceConfigurationId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.SavedSearch).WithMany(x => x.OpportunitySources)
            .HasForeignKey(x => x.SavedSearchId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.SourceExecution).WithMany(x => x.OpportunitySources)
            .HasForeignKey(x => x.SourceExecutionId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => x.OpportunityId);
        builder.HasIndex(x => x.SourceConfigurationId);
        builder.HasIndex(x => x.SavedSearchId);
        builder.HasIndex(x => x.SourceExecutionId);
        builder.HasIndex(x => x.SourceUrl);
        builder.HasIndex(x => new { x.SourceConfigurationId, x.ExternalId }).IsUnique()
            .HasDatabaseName("UX_OpportunitySources_SourceConfiguration_ExternalId")
            .HasFilter("\"SourceConfigurationId\" IS NOT NULL AND \"ExternalId\" IS NOT NULL");
        builder.HasIndex(x => new { x.OpportunityId, x.SourceUrl }).IsUnique()
            .HasDatabaseName("UX_OpportunitySources_Opportunity_SourceUrl")
            .HasFilter("\"SourceUrl\" IS NOT NULL");
        builder.ToTable("OpportunitySources", table =>
        {
            table.HasCheckConstraint("CK_OpportunitySources_LastSeenAt", "\"LastSeenAt\" IS NULL OR \"LastSeenAt\" >= \"FirstSeenAt\"");
        });
    }
}
