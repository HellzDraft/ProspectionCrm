using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ProspectionCrm.Api.Entities;

namespace ProspectionCrm.Api.Data.Configurations;

public sealed class SourceCollectionJobConfiguration : IEntityTypeConfiguration<SourceCollectionJob>
{
    public const string ActiveIndex = "UX_SourceCollectionJobs_Workspace_Search_Stage_Active";

    public void Configure(EntityTypeBuilder<SourceCollectionJob> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.TriggerTypeCode).IsRequired().HasMaxLength(50);
        builder.Property(x => x.StatusCode).IsRequired().HasMaxLength(50);
        builder.Property(x => x.ErrorCode).HasMaxLength(100);
        builder.HasOne(x => x.Workspace).WithMany(x => x.SourceCollectionJobs)
            .HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.SavedSearch).WithMany(x => x.SourceCollectionJobs)
            .HasForeignKey(x => new { x.WorkspaceId, x.SavedSearchId, x.PipelineId })
            .HasPrincipalKey(x => new { x.WorkspaceId, x.Id, x.PipelineId }).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.Pipeline).WithMany(x => x.SourceCollectionJobs)
            .HasForeignKey(x => new { x.WorkspaceId, x.PipelineId })
            .HasPrincipalKey(x => new { x.WorkspaceId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.PipelineStage).WithMany(x => x.SourceCollectionJobs)
            .HasForeignKey(x => new { x.PipelineId, x.PipelineStageId })
            .HasPrincipalKey(x => new { x.PipelineId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.SourceExecution).WithMany()
            .HasForeignKey(x => new { x.WorkspaceId, x.SourceExecutionId })
            .HasPrincipalKey(x => new { x.WorkspaceId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => new { x.StatusCode, x.AvailableAt, x.EnqueuedAt, x.Id });
        builder.HasIndex(x => new { x.WorkspaceId, x.EnqueuedAt, x.Id });
        builder.HasIndex(x => new { x.WorkspaceId, x.SavedSearchId, x.StatusCode, x.EnqueuedAt });
        builder.HasIndex(x => x.SourceExecutionId).IsUnique().HasFilter("\"SourceExecutionId\" IS NOT NULL");
        builder.HasIndex(x => new { x.WorkspaceId, x.SavedSearchId, x.PipelineStageId })
            .IsUnique().HasDatabaseName(ActiveIndex).HasFilter("\"StatusCode\" IN ('queued', 'running')");
        builder.ToTable("SourceCollectionJobs", table =>
        {
            table.HasCheckConstraint("CK_SourceCollectionJobs_Lease", """
                ("StatusCode" = 'running' AND "LeaseToken" IS NOT NULL
                    AND "LeaseToken" <> '00000000-0000-0000-0000-000000000000'::uuid
                    AND "LeaseExpiresAt" IS NOT NULL AND "LeaseExpiresAt" > "StartedAt")
                OR ("StatusCode" <> 'running' AND "LeaseToken" IS NULL AND "LeaseExpiresAt" IS NULL)
                """);
            table.HasCheckConstraint("CK_SourceCollectionJobs_TriggerTypeCode", "\"TriggerTypeCode\" IN ('manual', 'scheduled', 'event', 'retry')");
            table.HasCheckConstraint("CK_SourceCollectionJobs_StatusCode", "\"StatusCode\" IN ('queued', 'running', 'succeeded', 'failed', 'cancelled')");
            table.HasCheckConstraint("CK_SourceCollectionJobs_AttemptCount", "\"AttemptCount\" >= 0");
            table.HasCheckConstraint("CK_SourceCollectionJobs_AvailableAt", "\"AvailableAt\" >= \"EnqueuedAt\"");
            table.HasCheckConstraint("CK_SourceCollectionJobs_StartedAt", "\"StartedAt\" IS NULL OR \"StartedAt\" >= \"AvailableAt\"");
            table.HasCheckConstraint("CK_SourceCollectionJobs_FinishedAt", "\"FinishedAt\" IS NULL OR \"StartedAt\" IS NULL OR \"FinishedAt\" >= \"StartedAt\"");
            table.HasCheckConstraint("CK_SourceCollectionJobs_State", """
                ("StatusCode" = 'queued' AND "StartedAt" IS NULL AND "FinishedAt" IS NULL AND "SourceExecutionId" IS NULL AND "ErrorCode" IS NULL)
                OR ("StatusCode" = 'running' AND "StartedAt" IS NOT NULL AND "FinishedAt" IS NULL AND "ErrorCode" IS NULL)
                OR ("StatusCode" = 'succeeded' AND "StartedAt" IS NOT NULL AND "FinishedAt" IS NOT NULL AND "SourceExecutionId" IS NOT NULL AND "ErrorCode" IS NULL)
                OR ("StatusCode" = 'failed' AND "StartedAt" IS NOT NULL AND "FinishedAt" IS NOT NULL AND "ErrorCode" IS NOT NULL)
                OR ("StatusCode" = 'cancelled' AND "FinishedAt" IS NOT NULL)
                """);
        });
    }
}
