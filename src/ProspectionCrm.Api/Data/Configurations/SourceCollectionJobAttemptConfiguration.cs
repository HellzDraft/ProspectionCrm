using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ProspectionCrm.Api.Entities;

namespace ProspectionCrm.Api.Data.Configurations;

public sealed class SourceCollectionJobAttemptConfiguration : IEntityTypeConfiguration<SourceCollectionJobAttempt>
{
    public void Configure(EntityTypeBuilder<SourceCollectionJobAttempt> b)
    {
        b.HasKey(x => new { x.JobId, x.AttemptNumber });
        b.Property(x => x.StatusCode).HasMaxLength(50).IsRequired();
        b.Property(x => x.ErrorCode).HasMaxLength(100);
        b.HasOne(x => x.Job).WithMany().HasForeignKey(x => new { x.WorkspaceId, x.JobId })
            .HasPrincipalKey(x => new { x.WorkspaceId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.SourceExecution).WithMany().HasForeignKey(x => new { x.WorkspaceId, x.SourceExecutionId })
            .HasPrincipalKey(x => new { x.WorkspaceId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.SourceExecutionId).IsUnique().HasFilter("\"SourceExecutionId\" IS NOT NULL");
        b.ToTable("SourceCollectionJobAttempts", t =>
        {
            // Zero is reserved for historical Phase 7.1/7.2 rows with an unknown attempt number.
            t.HasCheckConstraint("CK_SourceCollectionJobAttempts_Number", "\"AttemptNumber\" >= 0");
            t.HasCheckConstraint("CK_SourceCollectionJobAttempts_Dates", "\"FinishedAt\" IS NULL OR \"FinishedAt\" >= \"StartedAt\"");
            t.HasCheckConstraint("CK_SourceCollectionJobAttempts_Http", "\"UpstreamStatusCode\" IS NULL OR \"UpstreamStatusCode\" BETWEEN 100 AND 599");
            t.HasCheckConstraint("CK_SourceCollectionJobAttempts_State", """
                ("StatusCode" = 'running' AND "FinishedAt" IS NULL)
                OR ("StatusCode" = 'succeeded' AND "FinishedAt" IS NOT NULL AND "SourceExecutionId" IS NOT NULL AND "ErrorCode" IS NULL)
                OR ("StatusCode" IN ('failed', 'cancelled') AND "FinishedAt" IS NOT NULL)
                """);
        });
    }
}
