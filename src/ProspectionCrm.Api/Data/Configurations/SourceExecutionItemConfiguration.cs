using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ProspectionCrm.Api.Entities;

namespace ProspectionCrm.Api.Data.Configurations;

public class SourceExecutionItemConfiguration : IEntityTypeConfiguration<SourceExecutionItem>
{
    public void Configure(EntityTypeBuilder<SourceExecutionItem> builder)
    {
        builder.HasKey(x => x.Id);
        builder.HasAlternateKey(x => new { x.WorkspaceId, x.Id });
        builder.Property(x => x.OutcomeCode).IsRequired().HasMaxLength(50);
        builder.Property(x => x.DecisionCode).IsRequired().HasMaxLength(100);
        builder.Property(x => x.Title).IsRequired().HasMaxLength(200);
        builder.Property(x => x.NormalizedTitle).IsRequired().HasMaxLength(400);
        builder.Property(x => x.CompanyName).HasMaxLength(200);
        builder.Property(x => x.NormalizedCompanyName).HasMaxLength(400);
        builder.Property(x => x.ExternalId).HasMaxLength(500);
        builder.Property(x => x.SourceUrl).HasMaxLength(2048);
        builder.Property(x => x.NormalizedSourceUrl).HasMaxLength(2048);
        builder.Property(x => x.PayloadSnapshotJson).HasColumnType("jsonb");
        builder.Property(x => x.DecisionDetailsJson).HasColumnType("jsonb");
        builder.HasOne(x => x.SourceExecution).WithMany(x => x.Items)
            .HasForeignKey(x => new { x.WorkspaceId, x.SourceExecutionId })
            .HasPrincipalKey(x => new { x.WorkspaceId, x.Id }).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(x => x.Opportunity).WithMany(x => x.SourceExecutionItems)
            .HasForeignKey(x => x.OpportunityId).OnDelete(DeleteBehavior.SetNull);
        builder.HasIndex(x => new { x.SourceExecutionId, x.ItemIndex }).IsUnique();
        builder.HasIndex(x => new { x.WorkspaceId, x.OpportunityId, x.ReceivedAt, x.Id });
        builder.HasIndex(x => new { x.SourceExecutionId, x.OutcomeCode, x.ItemIndex });
        builder.HasIndex(x => new { x.WorkspaceId, x.NormalizedTitle, x.NormalizedCompanyName })
            .HasFilter("""
                "NormalizedCompanyName" IS NOT NULL AND "OpportunityIdSnapshot" IS NOT NULL
                AND "OutcomeCode" IN ('created', 'updated', 'ignored')
                """);
        builder.ToTable("SourceExecutionItems", table =>
        {
            table.HasCheckConstraint("CK_SourceExecutionItems_ItemIndex", """
                "ItemIndex" >= 0
                """);
            table.HasCheckConstraint("CK_SourceExecutionItems_OutcomeCode", """
                "OutcomeCode" IN ('pending', 'created', 'updated', 'ignored', 'rejected', 'rolled-back', 'not-processed', 'cancelled')
                """);
            table.HasCheckConstraint("CK_SourceExecutionItems_ProcessedAt", """
                ("OutcomeCode" = 'pending' AND "ProcessedAt" IS NULL) OR
                ("OutcomeCode" <> 'pending' AND "ProcessedAt" IS NOT NULL AND "ProcessedAt" >= "ReceivedAt")
                """);
            table.HasCheckConstraint("CK_SourceExecutionItems_CompanyPair", """
                ("CompanyName" IS NULL) = ("NormalizedCompanyName" IS NULL)
                """);
            table.HasCheckConstraint("CK_SourceExecutionItems_UrlPair", """
                ("SourceUrl" IS NULL) = ("NormalizedSourceUrl" IS NULL)
                """);
            table.HasCheckConstraint("CK_SourceExecutionItems_OpportunitySnapshot", """
                "OpportunityId" IS NULL OR ("OpportunityIdSnapshot" IS NOT NULL AND "OpportunityId" = "OpportunityIdSnapshot")
                """);
            table.HasCheckConstraint("CK_SourceExecutionItems_OutcomeOpportunity", """
                ("OutcomeCode" NOT IN ('created', 'updated', 'ignored') OR "OpportunityIdSnapshot" IS NOT NULL)
                AND ("OutcomeCode" NOT IN ('rejected', 'rolled-back', 'not-processed', 'cancelled')
                     OR ("OpportunityId" IS NULL AND "OpportunityIdSnapshot" IS NULL))
                """);
            table.HasCheckConstraint("CK_SourceExecutionItems_PayloadSnapshotJson", """
                "PayloadSnapshotJson" IS NULL OR jsonb_typeof("PayloadSnapshotJson") = 'object'
                """);
            table.HasCheckConstraint("CK_SourceExecutionItems_DecisionDetailsJson", """
                "DecisionDetailsJson" IS NULL OR jsonb_typeof("DecisionDetailsJson") = 'object'
                """);
        });
    }
}
