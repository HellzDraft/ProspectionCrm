using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ProspectionCrm.Api.Entities;

namespace ProspectionCrm.Api.Data.Configurations;

public class SavedSearchConfiguration : IEntityTypeConfiguration<SavedSearch>
{
    public void Configure(EntityTypeBuilder<SavedSearch> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Name).IsRequired().HasMaxLength(200);
        builder.Property(x => x.SearchUrl).HasMaxLength(2048);
        builder.Property(x => x.CriteriaJson).IsRequired().HasColumnType("jsonb");
        builder.HasOne(x => x.Workspace).WithMany(x => x.SavedSearches)
            .HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.Pipeline).WithMany(x => x.SavedSearches)
            .HasForeignKey(x => x.PipelineId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.SourceConfiguration).WithMany(x => x.SavedSearches)
            .HasForeignKey(x => x.SourceConfigurationId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => new { x.WorkspaceId, x.ArchivedAt });
        builder.HasIndex(x => x.PipelineId);
        builder.HasIndex(x => x.SourceConfigurationId);
        builder.ToTable("SavedSearches", table => table.HasCheckConstraint(
            "CK_SavedSearches_CriteriaJson", "jsonb_typeof(\"CriteriaJson\") = 'object'"));
    }
}
