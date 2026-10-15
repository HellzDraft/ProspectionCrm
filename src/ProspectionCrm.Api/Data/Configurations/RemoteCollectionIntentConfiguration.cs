using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ProspectionCrm.Api.Entities;

namespace ProspectionCrm.Api.Data.Configurations;

public sealed class RemoteCollectionIntentConfiguration : IEntityTypeConfiguration<RemoteCollectionIntent>
{
    public void Configure(EntityTypeBuilder<RemoteCollectionIntent> b)
    {
        b.HasKey(x => x.JobId);
        b.HasOne(x => x.Job).WithOne(x => x.RemoteIntent).HasForeignKey<RemoteCollectionIntent>(x => new { x.WorkspaceId, x.JobId })
            .HasPrincipalKey<SourceCollectionJob>(x => new { x.WorkspaceId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<IntegrationOperation>().WithMany().HasForeignKey(x => x.OperationId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.OperationId).IsUnique();
        b.HasOne<SourceConfiguration>().WithMany().HasForeignKey(x => new { x.WorkspaceId, x.SourceConfigurationId })
            .HasPrincipalKey(x => new { x.WorkspaceId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.Property(x => x.OperationType).HasMaxLength(50);
        b.Property(x => x.Fingerprint).HasMaxLength(64);
        b.Property(x => x.ContextSnapshotJson).HasColumnType("text");
        b.Property(x => x.CommandJson).HasColumnType("text");
        // Phase941 also adds a PostgreSQL composite FK to the operation's nullable connection
        // using its unique index. EF alternate keys would incorrectly make historical NULLs required.
        b.ToTable("RemoteCollectionIntents", t =>
        {
            t.HasCheckConstraint("CK_RemoteCollectionIntents_Connection", "\"ConnectionId\" <> '00000000-0000-0000-0000-000000000000'::uuid AND \"OperationType\" = 'collection.request'");
            t.HasCheckConstraint("CK_RemoteCollectionIntents_Fingerprint", "\"Fingerprint\" ~ '^[0-9A-Fa-f]{64}$'");
            t.HasCheckConstraint("CK_RemoteCollectionIntents_Command", "octet_length(\"CommandJson\") BETWEEN 1 AND 4096");
        });
    }
}
