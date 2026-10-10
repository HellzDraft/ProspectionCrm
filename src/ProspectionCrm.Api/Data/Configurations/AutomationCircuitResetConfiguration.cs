using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ProspectionCrm.Api.Entities;

namespace ProspectionCrm.Api.Data.Configurations;

public sealed class AutomationCircuitResetConfiguration : IEntityTypeConfiguration<AutomationCircuitReset>
{
    public void Configure(EntityTypeBuilder<AutomationCircuitReset> b)
    {
        b.HasKey(x => x.Id);
        b.Property(x => x.Note).HasMaxLength(2000);
        b.HasOne(x => x.Workspace).WithMany().HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
        // UserAccount is global, without WorkspaceId. The V1 actor is resolved from the locked workspace.
        b.HasOne(x => x.RequestedByUser).WithMany().HasForeignKey(x => x.RequestedByUserId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.WorkspaceId, x.ResetAfterOutcomeSequence }).IsUnique();
        b.HasIndex(x => new { x.WorkspaceId, x.RequestedAt, x.Id }).IsDescending(false, true, true);
        b.ToTable("AutomationCircuitResets", t =>
            t.HasCheckConstraint("CK_AutomationCircuitResets_Sequence", "\"ResetAfterOutcomeSequence\" > 0"));
    }
}
