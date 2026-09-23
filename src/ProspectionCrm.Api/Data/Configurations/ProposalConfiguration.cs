using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ProspectionCrm.Api.Entities;

namespace ProspectionCrm.Api.Data.Configurations;

public class ProposalConfiguration : IEntityTypeConfiguration<Proposal>
{
    public void Configure(EntityTypeBuilder<Proposal> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.StatusCode).IsRequired().HasMaxLength(50);
        builder.Property(x => x.CurrencyCode).HasMaxLength(3);
        builder.Property(x => x.RateTypeCode).HasMaxLength(50);
        builder.Property(x => x.Notes).HasMaxLength(10000);
        builder.Property(x => x.Amount).HasPrecision(18, 2);
        builder.HasOne(x => x.Opportunity).WithMany(x => x.Proposals)
            .HasForeignKey(x => x.OpportunityId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(x => x.OpportunityId);
        builder.HasIndex(x => new { x.OpportunityId, x.StatusCode });
        builder.HasIndex(x => x.SentAt);
        builder.ToTable("Proposals", table =>
        {
            table.HasCheckConstraint("CK_Proposals_StatusCode", "\"StatusCode\" IN ('draft', 'sent', 'negotiating', 'accepted', 'rejected', 'expired', 'withdrawn')");
            table.HasCheckConstraint("CK_Proposals_Amount", "\"Amount\" IS NULL OR \"Amount\" >= 0");
            table.HasCheckConstraint("CK_Proposals_CurrencyCode", "\"CurrencyCode\" IS NULL OR (char_length(\"CurrencyCode\") = 3 AND \"CurrencyCode\" ~ '^[A-Z]{3}$')");
            table.HasCheckConstraint("CK_Proposals_AmountCurrency", "\"Amount\" IS NULL OR \"CurrencyCode\" IS NOT NULL");
            table.HasCheckConstraint("CK_Proposals_RateTypeCode", "\"RateTypeCode\" IS NULL OR \"RateTypeCode\" IN ('fixed', 'hourly', 'daily', 'monthly', 'other')");
            table.HasCheckConstraint("CK_Proposals_ValidUntil", "\"ValidUntil\" IS NULL OR \"SentAt\" IS NULL OR \"ValidUntil\" >= \"SentAt\"");
        });
    }
}
