using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ProspectionCrm.Api.Entities;

namespace ProspectionCrm.Api.Data.Configurations;

public class AutomationExecutionConfiguration : IEntityTypeConfiguration<AutomationExecution>
{
    public void Configure(EntityTypeBuilder<AutomationExecution> builder)
    {
        builder.HasKey(x => x.Id);
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
            table.HasCheckConstraint("CK_AutomationExecutions_ContextJson", "\"ContextJson\" IS NULL OR jsonb_typeof(\"ContextJson\") = 'object'");
            table.HasCheckConstraint("CK_AutomationExecutions_StatusCode", "\"StatusCode\" IN ('pending', 'running', 'succeeded', 'failed', 'cancelled', 'skipped')");
            table.HasCheckConstraint("CK_AutomationExecutions_StartedAt", "\"StartedAt\" IS NULL OR \"StartedAt\" >= \"TriggeredAt\"");
            table.HasCheckConstraint("CK_AutomationExecutions_FinishedAt", "\"FinishedAt\" IS NULL OR \"StartedAt\" IS NULL OR \"FinishedAt\" >= \"StartedAt\"");
        });
    }
}
