using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ProspectionCrm.Api.Entities;

namespace ProspectionCrm.Api.Data.Configurations;

public class AutomationExecutionConfiguration : IEntityTypeConfiguration<AutomationExecution>
{
    public void Configure(EntityTypeBuilder<AutomationExecution> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.ActionTypeCode).HasMaxLength(50);
        builder.Property(x => x.ReasonCode).HasMaxLength(100);
        builder.Property(x => x.IsAutomaticAttempt).HasDefaultValue(false);
        builder.Property(x => x.IsHumanApprovedAttempt).HasDefaultValue(false);
        builder.HasOne(x => x.AutomationActionRequest).WithMany()
            .HasForeignKey(x => new { x.WorkspaceId, x.AutomationJobId, x.AutomationRuleId, x.AutomationActionRequestId })
            .HasPrincipalKey(x => new { x.WorkspaceId, x.AutomationJobId, x.AutomationRuleId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        builder.Property(x => x.IsDeferred).HasDefaultValue(false);
        builder.Property(x => x.EffectApplied).HasDefaultValue(false);
        builder.HasOne(x => x.AutomationJob).WithMany()
            .HasForeignKey(x => new { x.WorkspaceId, x.AutomationJobId })
            .HasPrincipalKey(x => new { x.WorkspaceId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => new { x.AutomationJobId, x.AttemptNumber }).IsUnique()
            .HasDatabaseName("UX_AutomationExecutions_Job_Attempt").HasFilter("\"AutomationJobId\" IS NOT NULL");
        builder.HasIndex(x => x.AutomationJobId).IsUnique()
            .HasDatabaseName("UX_AutomationExecutions_Job_Effect").HasFilter("\"EffectApplied\"");
        builder.HasIndex(x => new { x.WorkspaceId, x.OutcomeSequence }).IsUnique()
            .HasDatabaseName("UX_AutomationExecutions_Workspace_Outcome").HasFilter("\"OutcomeSequence\" IS NOT NULL");
        builder.HasIndex(x => new { x.WorkspaceId, x.FinishedAt })
            .HasDatabaseName("IX_AutomationExecutions_Quotas").HasFilter("\"EffectApplied\" AND \"IsAutomaticAttempt\"");
        builder.Property(x => x.StatusCode).IsRequired().HasMaxLength(50);
        builder.Property(x => x.ErrorMessage).HasMaxLength(10000);
        builder.Property(x => x.ContextJson).HasColumnType("jsonb");
        builder.HasOne(x => x.Workspace).WithMany(x => x.AutomationExecutions)
            .HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.AutomationRule).WithMany(x => x.Executions)
            .HasForeignKey(x => x.AutomationRuleId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => new { x.WorkspaceId, x.TriggeredAt });
        builder.HasIndex(x => new { x.WorkspaceId, x.StatusCode, x.TriggeredAt });
        builder.HasIndex(x => new { x.AutomationRuleId, x.TriggeredAt });
        builder.ToTable("AutomationExecutions", table =>
        {
            table.HasCheckConstraint("CK_AutomationExecutions_Runtime", """
                ("AutomationJobId" IS NULL AND "AttemptNumber" IS NULL AND NOT "IsAutomaticAttempt"
                    AND NOT "IsDeferred" AND NOT "EffectApplied" AND "OutcomeSequence" IS NULL)
                OR ("AutomationJobId" IS NOT NULL AND "AttemptNumber" IS NOT NULL AND "AttemptNumber" > 0
                    AND "StartedAt" IS NOT NULL AND
                    (("StatusCode" = 'running' AND "FinishedAt" IS NULL)
                    OR ("StatusCode" IN ('succeeded','failed','skipped','cancelled') AND "FinishedAt" IS NOT NULL)))
                """);
            table.HasCheckConstraint("CK_AutomationExecutions_Effect", """
                NOT "EffectApplied" OR ("StatusCode" = 'succeeded' AND
                    (("IsAutomaticAttempt" AND "OutcomeSequence" IS NOT NULL)
                    OR ("IsHumanApprovedAttempt" AND "OutcomeSequence" IS NULL)))
                """);
            table.HasCheckConstraint("CK_AutomationExecutions_Human", """
                (NOT "IsHumanApprovedAttempt" AND "AutomationActionRequestId" IS NULL)
                OR ("IsHumanApprovedAttempt" AND NOT "IsAutomaticAttempt" AND "AutomationActionRequestId" IS NOT NULL
                    AND "AutomationJobId" IS NOT NULL AND "OutcomeSequence" IS NULL)
                """);
            table.HasCheckConstraint("CK_AutomationExecutions_Outcome", """
                "OutcomeSequence" IS NULL OR ("OutcomeSequence" > 0 AND "IsAutomaticAttempt"
                    AND ("EffectApplied" OR "StatusCode" = 'failed'))
                """);
            table.HasCheckConstraint("CK_AutomationExecutions_Deferred", """
                NOT "IsDeferred" OR ("StatusCode" = 'skipped' AND NOT "EffectApplied" AND "OutcomeSequence" IS NULL)
                """);
            table.HasCheckConstraint("CK_AutomationExecutions_ContextJson", "\"ContextJson\" IS NULL OR jsonb_typeof(\"ContextJson\") = 'object'");
            table.HasCheckConstraint("CK_AutomationExecutions_StatusCode", "\"StatusCode\" IN ('pending', 'running', 'succeeded', 'failed', 'cancelled', 'skipped')");
            table.HasCheckConstraint("CK_AutomationExecutions_StartedAt", "\"StartedAt\" IS NULL OR \"StartedAt\" >= \"TriggeredAt\"");
            table.HasCheckConstraint("CK_AutomationExecutions_FinishedAt", "\"FinishedAt\" IS NULL OR \"StartedAt\" IS NULL OR \"FinishedAt\" >= \"StartedAt\"");
        });
    }
}
