using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ProspectionCrm.Api.Entities;

namespace ProspectionCrm.Api.Data.Configurations;

public class ActivityEntryConfiguration : IEntityTypeConfiguration<ActivityEntry>
{
    public void Configure(EntityTypeBuilder<ActivityEntry> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.EntityTypeCode).IsRequired().HasMaxLength(100);
        builder.Property(x => x.EventTypeCode).IsRequired().HasMaxLength(100);
        builder.Property(x => x.ActorTypeCode).IsRequired().HasMaxLength(50);
        builder.Property(x => x.Summary).HasMaxLength(2000);
        builder.Property(x => x.MetadataJson).HasColumnType("jsonb");
        builder.HasOne(x => x.Workspace).WithMany(x => x.ActivityEntries)
            .HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.RelatedOpportunity).WithMany(x => x.ActivityEntries)
            .HasForeignKey(x => x.RelatedOpportunityId).OnDelete(DeleteBehavior.SetNull);
        builder.HasOne(x => x.ActorUser).WithMany()
            .HasForeignKey(x => x.ActorUserId).OnDelete(DeleteBehavior.SetNull);
        builder.HasIndex(x => new { x.WorkspaceId, x.OccurredAt });
        builder.HasIndex(x => new { x.WorkspaceId, x.EntityTypeCode, x.EntityId, x.OccurredAt });
        builder.HasIndex(x => x.RelatedOpportunityId);
        builder.HasIndex(x => x.ActorUserId);
        builder.HasIndex(x => new { x.WorkspaceId, x.EventTypeCode, x.OccurredAt });
        builder.ToTable("ActivityEntries", table =>
        {
            table.HasCheckConstraint("CK_ActivityEntries_MetadataJson", "\"MetadataJson\" IS NULL OR jsonb_typeof(\"MetadataJson\") = 'object'");
        });
    }
}
