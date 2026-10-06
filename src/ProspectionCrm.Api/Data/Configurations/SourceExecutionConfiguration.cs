using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ProspectionCrm.Api.Entities;

namespace ProspectionCrm.Api.Data.Configurations;

public class SourceExecutionConfiguration : IEntityTypeConfiguration<SourceExecution>
{
    public void Configure(EntityTypeBuilder<SourceExecution> builder)
    {
        builder.HasKey(x => x.Id);
        builder.HasAlternateKey(x => new { x.WorkspaceId, x.Id });
        builder.Property(x => x.ContextSnapshotJson).HasColumnType("jsonb");
        builder.Property(x => x.TriggerTypeCode).IsRequired().HasMaxLength(50);
        builder.Property(x => x.StatusCode).IsRequired().HasMaxLength(50);
        builder.Property(x => x.ErrorMessage).HasMaxLength(10000);
        builder.HasOne(x => x.Workspace).WithMany(x => x.SourceExecutions)
            .HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.SourceConfiguration).WithMany(x => x.Executions)
            .HasForeignKey(x => x.SourceConfigurationId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.SavedSearch).WithMany(x => x.Executions)
            .HasForeignKey(x => x.SavedSearchId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => new { x.WorkspaceId, x.StartedAt });
        builder.HasIndex(x => new { x.WorkspaceId, x.StatusCode, x.StartedAt });
        builder.HasIndex(x => new { x.SourceConfigurationId, x.StartedAt });
        builder.HasIndex(x => new { x.SavedSearchId, x.StartedAt });
        builder.ToTable("SourceExecutions", table =>
        {
            table.HasCheckConstraint("CK_SourceExecutions_ContextSnapshotJson", """
                "ContextSnapshotJson" IS NULL OR jsonb_typeof("ContextSnapshotJson") = 'object'
                """);
            table.HasCheckConstraint("CK_SourceExecutions_HistoryVersions", """
                ("HistoryVersion" IS NULL AND "ContractVersion" IS NULL AND "NormalizationVersion" IS NULL
                 AND "TargetPipelineId" IS NULL AND "TargetPipelineStageId" IS NULL AND "ContextSnapshotJson" IS NULL)
                OR ("HistoryVersion" IS NOT NULL AND "HistoryVersion" = 1
                    AND "ContractVersion" IS NOT NULL AND "ContractVersion" = 1
                    AND "NormalizationVersion" IS NOT NULL AND "NormalizationVersion" = 1
                    AND "TargetPipelineId" IS NOT NULL AND "TargetPipelineStageId" IS NOT NULL AND "ContextSnapshotJson" IS NOT NULL)
                """);
            table.HasCheckConstraint("CK_SourceExecutions_HistoryCounters", """
                "ItemsRejected" >= 0 AND "ItemsRolledBack" >= 0 AND "ItemsNotProcessed" >= 0 AND "ItemsCancelled" >= 0
                """);
            table.HasCheckConstraint("CK_SourceExecutions_TerminalCounters", """
                "HistoryVersion" IS NULL OR "StatusCode" = 'running' OR
                "ItemsFound"::bigint = "ItemsCreated"::bigint + "ItemsUpdated" + "ItemsIgnored"
                    + "ItemsRejected" + "ItemsRolledBack" + "ItemsNotProcessed" + "ItemsCancelled"
                """);
            table.HasCheckConstraint("CK_SourceExecutions_OutcomeCounters", """
                "HistoryVersion" IS NULL OR (
                    ("StatusCode" <> 'succeeded' OR ("ItemsRejected" = 0 AND "ItemsRolledBack" = 0 AND "ItemsNotProcessed" = 0 AND "ItemsCancelled" = 0))
                    AND ("StatusCode" <> 'failed' OR ("ItemsCreated" = 0 AND "ItemsUpdated" = 0 AND "ItemsIgnored" = 0 AND "ItemsCancelled" = 0))
                    AND ("StatusCode" <> 'cancelled' OR ("ItemsCreated" = 0 AND "ItemsUpdated" = 0 AND "ItemsIgnored" = 0 AND "ItemsRejected" = 0)))
                """);
            table.HasCheckConstraint("CK_SourceExecutions_TriggerTypeCode", "\"TriggerTypeCode\" IN ('manual', 'scheduled', 'event', 'retry')");
            table.HasCheckConstraint("CK_SourceExecutions_StatusCode", "\"StatusCode\" IN ('running', 'succeeded', 'partial', 'failed', 'cancelled')");
            table.HasCheckConstraint("CK_SourceExecutions_ItemsFound", "\"ItemsFound\" >= 0");
            table.HasCheckConstraint("CK_SourceExecutions_ItemsCreated", "\"ItemsCreated\" >= 0");
            table.HasCheckConstraint("CK_SourceExecutions_ItemsUpdated", "\"ItemsUpdated\" >= 0");
            table.HasCheckConstraint("CK_SourceExecutions_ItemsIgnored", "\"ItemsIgnored\" >= 0");
            table.HasCheckConstraint("CK_SourceExecutions_FinishedAt", "\"FinishedAt\" IS NULL OR \"FinishedAt\" >= \"StartedAt\"");
        });
    }
}
