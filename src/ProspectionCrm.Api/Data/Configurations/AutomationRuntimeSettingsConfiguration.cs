using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ProspectionCrm.Api.Entities;
using ProspectionCrm.Api.Services.Automation;

namespace ProspectionCrm.Api.Data.Configurations;

public sealed class AutomationRuntimeSettingsConfiguration : IEntityTypeConfiguration<AutomationRuntimeSettings>
{
    public void Configure(EntityTypeBuilder<AutomationRuntimeSettings> b)
    {
        b.HasKey(x => x.WorkspaceId);
        b.Property(x => x.WorkspaceId).ValueGeneratedNever();
        b.HasOne(x => x.Workspace).WithOne(x => x.AutomationRuntimeSettings)
            .HasForeignKey<AutomationRuntimeSettings>(x => x.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
        b.Property(x => x.IsEnabled).HasDefaultValue(false).ValueGeneratedNever();
        b.Property(x => x.OperatingModeCode).IsRequired().HasMaxLength(20).HasDefaultValue(AutomationModes.Manual).ValueGeneratedNever();
        b.Property(x => x.MaxExecutionsPerMinute).HasDefaultValue(10).ValueGeneratedNever();
        b.Property(x => x.MaxExecutionsPerDay).HasDefaultValue(100).ValueGeneratedNever();
        b.Property(x => x.MaxConsecutiveFailures).HasDefaultValue(3).ValueGeneratedNever();
        b.Property(x => x.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
        b.ToTable("AutomationRuntimeSettings", t =>
        {
            t.HasCheckConstraint("CK_AutomationRuntimeSettings_Mode", $"\"OperatingModeCode\" IN ('{AutomationModes.Manual}', '{AutomationModes.Assist}', '{AutomationModes.Automatic}')");
            t.HasCheckConstraint("CK_AutomationRuntimeSettings_PerMinute", "\"MaxExecutionsPerMinute\" BETWEEN 1 AND 100");
            t.HasCheckConstraint("CK_AutomationRuntimeSettings_PerDay", "\"MaxExecutionsPerDay\" BETWEEN 1 AND 10000 AND \"MaxExecutionsPerDay\" >= \"MaxExecutionsPerMinute\"");
            t.HasCheckConstraint("CK_AutomationRuntimeSettings_Failures", "\"MaxConsecutiveFailures\" BETWEEN 1 AND 20");
        });
    }
}
