using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ProspectionCrm.Api.Entities;

namespace ProspectionCrm.Api.Data.Configurations;

public class DocumentConfiguration : IEntityTypeConfiguration<Document>
{
    public void Configure(EntityTypeBuilder<Document> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.KindCode).IsRequired().HasMaxLength(50);
        builder.Property(x => x.OriginalFileName).IsRequired().HasMaxLength(500);
        builder.Property(x => x.StorageKey).IsRequired().HasMaxLength(1000);
        builder.Property(x => x.ContentType).IsRequired().HasMaxLength(200);
        builder.Property(x => x.Sha256).HasMaxLength(64);
        builder.HasOne(x => x.Workspace).WithMany(x => x.Documents)
            .HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => new { x.WorkspaceId, x.ArchivedAt });
        builder.HasIndex(x => new { x.WorkspaceId, x.KindCode });
        builder.HasIndex(x => new { x.WorkspaceId, x.Sha256 });
        builder.HasIndex(x => new { x.WorkspaceId, x.StorageKey }).IsUnique()
            .HasDatabaseName("UX_Documents_Workspace_StorageKey");
        builder.ToTable("Documents", table =>
        {
            table.HasCheckConstraint("CK_Documents_SizeBytes", "\"SizeBytes\" >= 0");
            table.HasCheckConstraint("CK_Documents_Sha256", "\"Sha256\" IS NULL OR (char_length(\"Sha256\") = 64 AND \"Sha256\" ~ '^[0-9A-Fa-f]{64}$')");
        });
    }
}
