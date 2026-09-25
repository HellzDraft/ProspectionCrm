using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ProspectionCrm.Api.Entities;

namespace ProspectionCrm.Api.Data.Configurations;

public class ScoringRuleConfiguration : IEntityTypeConfiguration<ScoringRule>
{
    public void Configure(EntityTypeBuilder<ScoringRule> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Name).IsRequired().HasMaxLength(200);
        builder.Property(x => x.Description).HasMaxLength(2000);
        builder.Property(x => x.RuleTypeCode).IsRequired().HasMaxLength(50);
        builder.Property(x => x.ConfigurationJson).IsRequired().HasColumnType("jsonb");
        builder.HasOne(x => x.Workspace).WithMany(x => x.ScoringRules)
            .HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.Pipeline).WithMany(x => x.ScoringRules)
            .HasForeignKey(x => x.PipelineId).OnDelete(DeleteBehavior.Restrict);
        builder.Property(x => x.Weight).HasPrecision(8, 2);
        builder.HasIndex(x => new { x.WorkspaceId, x.ArchivedAt });
        builder.HasIndex(x => new { x.WorkspaceId, x.Enabled });
        builder.HasIndex(x => x.PipelineId);
        builder.HasIndex(x => new { x.PipelineId, x.Enabled });
        builder.ToTable("ScoringRules", table =>
        {
            table.HasCheckConstraint("CK_ScoringRules_ConfigurationJson", "jsonb_typeof(\"ConfigurationJson\") = 'object'");
            table.HasCheckConstraint("CK_ScoringRules_Weight", "\"Weight\" >= -100 AND \"Weight\" <= 100");
        });
    }
}
