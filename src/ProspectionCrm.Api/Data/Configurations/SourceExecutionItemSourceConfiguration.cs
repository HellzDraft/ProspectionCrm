using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ProspectionCrm.Api.Entities;

namespace ProspectionCrm.Api.Data.Configurations;

public class SourceExecutionItemSourceConfiguration : IEntityTypeConfiguration<SourceExecutionItemSource>
{
    public void Configure(EntityTypeBuilder<SourceExecutionItemSource> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.RoleCode).IsRequired().HasMaxLength(50);
        builder.HasOne(x => x.SourceExecutionItem).WithMany(x => x.Sources)
            .HasForeignKey(x => new { x.WorkspaceId, x.SourceExecutionItemId })
            .HasPrincipalKey(x => new { x.WorkspaceId, x.Id }).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(x => x.OpportunitySource).WithMany(x => x.ExecutionItemSources)
            .HasForeignKey(x => x.OpportunitySourceId).OnDelete(DeleteBehavior.SetNull);
        builder.HasIndex(x => new { x.SourceExecutionItemId, x.OpportunitySourceIdSnapshot, x.RoleCode }).IsUnique();
        builder.HasIndex(x => new { x.WorkspaceId, x.OpportunitySourceId });
        builder.HasIndex(x => new { x.SourceExecutionItemId, x.RoleCode });
        builder.ToTable("SourceExecutionItemSources", table =>
        {
            table.HasCheckConstraint("CK_SourceExecutionItemSources_RoleCode", """
                "RoleCode" IN ('external-id', 'source-url')
                """);
            table.HasCheckConstraint("CK_SourceExecutionItemSources_SourceSnapshot", """
                "OpportunitySourceId" IS NULL OR "OpportunitySourceId" = "OpportunitySourceIdSnapshot"
                """);
        });
    }
}
