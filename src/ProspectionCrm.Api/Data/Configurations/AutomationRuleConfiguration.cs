using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ProspectionCrm.Api.Entities;

namespace ProspectionCrm.Api.Data.Configurations;

public class AutomationRuleConfiguration : IEntityTypeConfiguration<AutomationRule>
{
    public void Configure(EntityTypeBuilder<AutomationRule> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Name).IsRequired().HasMaxLength(200);
        builder.Property(x => x.Description).HasMaxLength(2000);
        builder.Property(x => x.TriggerTypeCode).IsRequired().HasMaxLength(50);
        builder.Property(x => x.ConditionJson).HasColumnType("jsonb");
        builder.Property(x => x.ActionTypeCode).IsRequired().HasMaxLength(50);
        builder.Property(x => x.ActionConfigurationJson).HasColumnType("jsonb");
        builder.HasOne(x => x.Workspace).WithMany(x => x.AutomationRules)
            .HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.Pipeline).WithMany(x => x.AutomationRules)
            .HasForeignKey(x => x.PipelineId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => new { x.WorkspaceId, x.ArchivedAt });
        builder.HasIndex(x => new { x.WorkspaceId, x.Enabled });
        builder.HasIndex(x => x.PipelineId);
        builder.HasIndex(x => new { x.PipelineId, x.Enabled });
        builder.HasIndex(x => new { x.WorkspaceId, x.TriggerTypeCode });
        builder.ToTable("AutomationRules", table =>
        {
            table.HasCheckConstraint("CK_AutomationRules_ConditionJson", "\"ConditionJson\" IS NULL OR jsonb_typeof(\"ConditionJson\") = 'object'");
            table.HasCheckConstraint("CK_AutomationRules_ActionConfigurationJson", "\"ActionConfigurationJson\" IS NULL OR jsonb_typeof(\"ActionConfigurationJson\") = 'object'");
        });
    }
}
