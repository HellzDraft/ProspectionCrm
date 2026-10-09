using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ProspectionCrm.Api.Entities;
using ProspectionCrm.Api.Services.Automation;

namespace ProspectionCrm.Api.Data.Configurations;

public sealed class AutomationActionRequestConfiguration : IEntityTypeConfiguration<AutomationActionRequest>
{
    public void Configure(EntityTypeBuilder<AutomationActionRequest> b)
    {
        b.HasKey(x => x.Id);
        b.HasAlternateKey(x => new { x.WorkspaceId, x.AutomationJobId, x.AutomationRuleId, x.Id });
        b.HasIndex(x => x.AutomationJobId).IsUnique().HasDatabaseName("UX_AutomationActionRequests_Job");
        b.Property(x => x.DecisionRequirementCode).HasMaxLength(20).IsRequired();
        b.Property(x => x.StatusCode).HasMaxLength(20).IsRequired().HasDefaultValue(ActionRequestStatuses.Pending).ValueGeneratedNever();
        b.Property(x => x.ActionTypeCode).HasMaxLength(50).IsRequired();
        b.Property(x => x.ActionCategoryCode).HasMaxLength(50).IsRequired();
        b.Property(x => x.ActionPlanJson).HasColumnType("jsonb").IsRequired();
        b.Property(x => x.RuleFingerprint).HasMaxLength(64).IsRequired();
        b.Property(x => x.RequestedReasonCode).HasMaxLength(100).IsRequired();
        b.Property(x => x.DecisionNote).HasMaxLength(ActionRequestCodes.NoteLength);
        b.HasOne(x => x.Workspace).WithMany().HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.AutomationJob).WithMany().HasForeignKey(x => new { x.WorkspaceId, x.AutomationJobId })
            .HasPrincipalKey(x => new { x.WorkspaceId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.AutomationRule).WithMany().HasForeignKey(x => new { x.WorkspaceId, x.AutomationRuleId })
            .HasPrincipalKey(x => new { x.WorkspaceId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.DecidedByUser).WithMany().HasForeignKey(x => x.DecidedByUserId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.WorkspaceId, x.RequestedAt, x.Id }).IsDescending(false, true, true);
        b.HasIndex(x => new { x.WorkspaceId, x.StatusCode, x.RequestedAt, x.Id }).IsDescending(false, false, true, true);
        b.ToTable("AutomationActionRequests", t =>
        {
            t.HasCheckConstraint("CK_ActionRequests_Requirement", "\"DecisionRequirementCode\" IN ('manual','approval-required')");
            t.HasCheckConstraint("CK_ActionRequests_Status", "\"StatusCode\" IN ('pending','approved','rejected','cancelled')");
            t.HasCheckConstraint("CK_ActionRequests_Plan", $"jsonb_typeof(\"ActionPlanJson\") = 'object' AND octet_length(\"ActionPlanJson\"::text) <= {ActionRequestCodes.PlanBytes}");
            t.HasCheckConstraint("CK_ActionRequests_Fingerprint", "\"RuleFingerprint\" ~ '^[0-9a-f]{64}$'");
            t.HasCheckConstraint("CK_ActionRequests_Reason", "length(btrim(\"RequestedReasonCode\")) > 0");
            t.HasCheckConstraint("CK_ActionRequests_Decision", """
                ("StatusCode" = 'pending' AND "DecidedAt" IS NULL AND "DecidedByUserId" IS NULL AND "DecisionNote" IS NULL)
                OR ("StatusCode" IN ('approved','rejected') AND "DecidedAt" IS NOT NULL AND "DecidedByUserId" IS NOT NULL)
                OR ("StatusCode" = 'cancelled' AND "DecidedAt" IS NOT NULL)
                """);
            t.HasCheckConstraint("CK_ActionRequests_Dates", "\"DecidedAt\" IS NULL OR \"DecidedAt\" >= \"RequestedAt\"");
        });
    }
}
