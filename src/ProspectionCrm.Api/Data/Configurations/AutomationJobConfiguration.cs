using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ProspectionCrm.Api.Entities;
using ProspectionCrm.Api.Services.Automation;

namespace ProspectionCrm.Api.Data.Configurations;

public sealed class AutomationJobConfiguration : IEntityTypeConfiguration<AutomationJob>
{
    public const string DeduplicationIndex = "UX_AutomationJobs_Workspace_Trigger_Key";
    public void Configure(EntityTypeBuilder<AutomationJob> b)
    {
        b.HasKey(x => x.Id);
        b.HasAlternateKey(x => new { x.WorkspaceId, x.Id });
        b.Property(x => x.TriggerTypeCode).IsRequired().HasMaxLength(50);
        b.Property(x => x.TriggerKey).HasMaxLength(AutomationJobLimits.TriggerKeyLength);
        b.Property(x => x.ActionCategoryCode).IsRequired().HasMaxLength(50);
        b.Property(x => x.StatusCode).IsRequired().HasMaxLength(20)
            .HasDefaultValue(AutomationJobStatuses.Pending).ValueGeneratedNever();
        b.Property(x => x.Priority).HasDefaultValue(AutomationJobLimits.DefaultPriority).ValueGeneratedNever();
        b.Property(x => x.AttemptCount).HasDefaultValue(0).ValueGeneratedNever();
        b.Property(x => x.LeaseOwner).HasMaxLength(100);
        b.Property(x => x.LastError).HasMaxLength(AutomationJobLimits.LastErrorLength);
        b.Property(x => x.ContextJson).HasColumnType("jsonb");
        b.Property(x => x.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
        b.Property(x => x.AvailableAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
        b.HasOne(x => x.Workspace).WithMany().HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.AutomationRule).WithMany().HasForeignKey(x => new { x.WorkspaceId, x.AutomationRuleId })
            .HasPrincipalKey(x => new { x.WorkspaceId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.WorkspaceId, x.TriggerTypeCode, x.TriggerKey }).IsUnique()
            .HasDatabaseName(DeduplicationIndex).HasFilter("\"TriggerKey\" IS NOT NULL");
        b.HasIndex(x => new { x.WorkspaceId, x.Priority, x.AvailableAt, x.CreatedAt, x.Id })
            .IsDescending(false, true, false, false, false).HasDatabaseName("IX_AutomationJobs_Claim")
            .HasFilter($"\"StatusCode\" = '{AutomationJobStatuses.Pending}'");
        b.HasIndex(x => new { x.WorkspaceId, x.LeaseExpiresAt }).HasDatabaseName("IX_AutomationJobs_Recovery")
            .HasFilter($"\"StatusCode\" = '{AutomationJobStatuses.Leased}'");
        b.HasIndex(x => new { x.WorkspaceId, x.CreatedAt, x.Id }).IsDescending(false, true, true);
        b.ToTable("AutomationJobs", t =>
        {
            t.HasCheckConstraint("CK_AutomationJobs_Status", $"\"StatusCode\" IN ('{AutomationJobStatuses.Pending}', '{AutomationJobStatuses.Leased}', '{AutomationJobStatuses.Completed}', '{AutomationJobStatuses.Failed}', '{AutomationJobStatuses.Cancelled}', '{AutomationJobStatuses.AwaitingApproval}')");
            t.HasCheckConstraint("CK_AutomationJobs_Priority", $"\"Priority\" BETWEEN {AutomationJobLimits.MinPriority} AND {AutomationJobLimits.MaxPriority}");
            t.HasCheckConstraint("CK_AutomationJobs_Attempts", "\"AttemptCount\" >= 0");
            t.HasCheckConstraint("CK_AutomationJobs_Trigger", $"\"TriggerTypeCode\" IN ('{AutomationJobTriggers.Manual}')");
            t.HasCheckConstraint("CK_AutomationJobs_Category", $"\"ActionCategoryCode\" IN ('{AutomationCategories.General}', '{AutomationCategories.Email}', '{AutomationCategories.Application}')");
            t.HasCheckConstraint("CK_AutomationJobs_Lease", $"""
                ("StatusCode" = '{AutomationJobStatuses.Leased}' AND "LeaseOwner" IS NOT NULL
                    AND length(btrim("LeaseOwner")) > 0 AND "LeaseExpiresAt" IS NOT NULL)
                OR ("StatusCode" <> '{AutomationJobStatuses.Leased}' AND "LeaseOwner" IS NULL AND "LeaseExpiresAt" IS NULL)
                """);
            t.HasCheckConstraint("CK_AutomationJobs_CompletedAt", $"""
                ("StatusCode" IN ('{AutomationJobStatuses.Completed}', '{AutomationJobStatuses.Failed}', '{AutomationJobStatuses.Cancelled}') AND "CompletedAt" IS NOT NULL)
                OR ("StatusCode" IN ('{AutomationJobStatuses.Pending}', '{AutomationJobStatuses.Leased}', '{AutomationJobStatuses.AwaitingApproval}') AND "CompletedAt" IS NULL)
                """);
            t.HasCheckConstraint("CK_AutomationJobs_Context", $"\"ContextJson\" IS NULL OR (jsonb_typeof(\"ContextJson\") = 'object' AND octet_length(\"ContextJson\"::text) <= {AutomationJobLimits.StoredContextBytes})");
            t.HasCheckConstraint("CK_AutomationJobs_Error", $"\"LastError\" IS NULL OR (\"StatusCode\" = '{AutomationJobStatuses.Failed}' AND \"LastError\" IN ('{AutomationJobErrors.JobFailed}', '{AutomationJobErrors.ProcessingRejected}'))");
        });
    }
}
