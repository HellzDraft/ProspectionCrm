using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ProspectionCrm.Api.Entities;

namespace ProspectionCrm.Api.Data.Configurations;

public sealed class SourceCollectionScheduleConfiguration : IEntityTypeConfiguration<SourceCollectionSchedule>
{
    public void Configure(EntityTypeBuilder<SourceCollectionSchedule> builder)
    {
        builder.HasKey(x => x.SavedSearchId);
        builder.HasOne(x => x.SavedSearch).WithOne(x => x.CollectionSchedule)
            .HasForeignKey<SourceCollectionSchedule>(x => new { x.WorkspaceId, x.SavedSearchId, x.PipelineId })
            .HasPrincipalKey<SavedSearch>(x => new { x.WorkspaceId, x.Id, x.PipelineId }).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Pipeline>().WithMany().HasForeignKey(x => new { x.WorkspaceId, x.PipelineId })
            .HasPrincipalKey(x => new { x.WorkspaceId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<PipelineStage>().WithMany().HasForeignKey(x => new { x.PipelineId, x.PipelineStageId })
            .HasPrincipalKey(x => new { x.PipelineId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => new { x.NextCollectionAt, x.SavedSearchId }).HasFilter("\"Enabled\" = TRUE");
        builder.ToTable("SourceCollectionSchedules", table =>
        {
            table.HasCheckConstraint("CK_SourceCollectionSchedules_Minute", "\"DailyUtcMinute\" BETWEEN 0 AND 1439");
            table.HasCheckConstraint("CK_SourceCollectionSchedules_State",
                "(\"Enabled\" AND \"NextCollectionAt\" IS NOT NULL) OR (NOT \"Enabled\" AND \"NextCollectionAt\" IS NULL)");
        });
    }
}
