using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ProspectionCrm.Api.Entities;

namespace ProspectionCrm.Api.Data.Configurations;

public class OpportunityConfiguration : IEntityTypeConfiguration<Opportunity>
{
    public void Configure(EntityTypeBuilder<Opportunity> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Title).IsRequired().HasMaxLength(200);
        builder.Property(x => x.PriorityCode).IsRequired().HasMaxLength(50);
        builder.Property(x => x.Location).HasMaxLength(200);
        builder.Property(x => x.Notes).HasMaxLength(10000);
        builder.Property(x => x.Score).HasPrecision(5, 2);
        builder.HasOne(x => x.Workspace).WithMany(x => x.Opportunities)
            .HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.PipelineStage).WithMany(x => x.Opportunities)
            .HasForeignKey(x => x.PipelineStageId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.Company).WithMany(x => x.Opportunities)
            .HasForeignKey(x => x.CompanyId).OnDelete(DeleteBehavior.SetNull);
        builder.HasOne(x => x.Contact).WithMany(x => x.Opportunities)
            .HasForeignKey(x => x.ContactId).OnDelete(DeleteBehavior.SetNull);
        builder.HasIndex(x => new { x.WorkspaceId, x.PipelineStageId });
        builder.HasIndex(x => new { x.WorkspaceId, x.CompanyId });
        builder.HasIndex(x => new { x.WorkspaceId, x.ContactId });
        builder.HasIndex(x => new { x.WorkspaceId, x.ArchivedAt });
        builder.ToTable("Opportunities", table =>
        {
            table.HasCheckConstraint("CK_Opportunities_PriorityCode", "\"PriorityCode\" IN ('low', 'normal', 'high')");
            table.HasCheckConstraint("CK_Opportunities_Score", "\"Score\" IS NULL OR (\"Score\" >= 0 AND \"Score\" <= 100)");
        });
    }
}
