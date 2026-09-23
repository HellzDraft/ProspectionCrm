using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ProspectionCrm.Api.Entities;

namespace ProspectionCrm.Api.Data.Configurations;

public class CampaignOpportunityConfiguration : IEntityTypeConfiguration<CampaignOpportunity>
{
    public void Configure(EntityTypeBuilder<CampaignOpportunity> builder)
    {
        builder.HasKey(x => x.Id);
        builder.HasOne(x => x.Campaign).WithMany(x => x.CampaignOpportunities)
            .HasForeignKey(x => x.CampaignId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(x => x.Opportunity).WithMany(x => x.CampaignOpportunities)
            .HasForeignKey(x => x.OpportunityId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(x => x.OpportunityId);
        builder.HasIndex(x => new { x.CampaignId, x.OpportunityId }).IsUnique();
    }
}
