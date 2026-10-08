using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ProspectionCrm.Api.Entities;

namespace ProspectionCrm.Api.Data.Configurations;

public class CrmTaskConfiguration : IEntityTypeConfiguration<CrmTask>
{
    public void Configure(EntityTypeBuilder<CrmTask> builder)
    {
        builder.HasKey(x => x.Id);
        builder.HasOne(x => x.AutomationJob).WithMany().HasForeignKey(x => x.AutomationJobId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => x.AutomationJobId).IsUnique()
            .HasDatabaseName("UX_CrmTasks_AutomationJob").HasFilter("\"AutomationJobId\" IS NOT NULL");
        builder.Property(x => x.Title).IsRequired().HasMaxLength(200);
        builder.Property(x => x.Description).HasMaxLength(10000);
        builder.HasOne(x => x.Opportunity).WithMany(x => x.CrmTasks)
            .HasForeignKey(x => x.OpportunityId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(x => x.OpportunityId);
        builder.HasIndex(x => new { x.IsCompleted, x.DueAt });
    }
}
