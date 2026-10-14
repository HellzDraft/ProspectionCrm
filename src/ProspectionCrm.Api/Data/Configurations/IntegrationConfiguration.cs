using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ProspectionCrm.Api.Dtos.Integrations;
using ProspectionCrm.Api.Entities;

namespace ProspectionCrm.Api.Data.Configurations;

public sealed class IntegrationOperationConfiguration : IEntityTypeConfiguration<IntegrationOperation>
{
    public void Configure(EntityTypeBuilder<IntegrationOperation> b)
    {
        b.HasKey(x => x.Id);
        b.HasIndex(x => x.CommandMessageId).IsUnique();
        b.HasIndex(x => new { x.WorkspaceId, x.StatusCode, x.CreatedAt });
        b.HasOne<Workspace>().WithMany().HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
        b.Property(x => x.Type).HasMaxLength(50).IsRequired();
        b.Property(x => x.StatusCode).HasMaxLength(24).IsRequired();
        b.Property(x => x.Revision).IsConcurrencyToken();
        b.ToTable("IntegrationOperations", t =>
        {
            t.HasCheckConstraint("CK_IntegrationOperations_Connection", "\"ConnectionId\" IS NULL OR \"ConnectionId\" <> '00000000-0000-0000-0000-000000000000'::uuid");
            t.HasCheckConstraint("CK_IntegrationOperations_Contract", "\"Version\" = 1 AND \"Type\" = 'technical.probe'");
            t.HasCheckConstraint("CK_IntegrationOperations_Status", "\"StatusCode\" IN ('pending','awaiting-result','result-received','succeeded','failed')");
            t.HasCheckConstraint("CK_IntegrationOperations_Revision", "\"Revision\" >= 0");
            t.HasCheckConstraint("CK_IntegrationOperations_Completion", """
                ("StatusCode" IN ('succeeded','failed')) = ("CompletedAt" IS NOT NULL)
                AND ("CompletedAt" IS NULL OR "CompletedAt" >= "CreatedAt")
                """);
            t.HasCheckConstraint("CK_IntegrationOperations_Ids", """
                "Id" <> '00000000-0000-0000-0000-000000000000'::uuid
                AND "CommandMessageId" <> '00000000-0000-0000-0000-000000000000'::uuid
                AND "CorrelationId" <> '00000000-0000-0000-0000-000000000000'::uuid
                AND "ProbeId" <> '00000000-0000-0000-0000-000000000000'::uuid
                """);
        });
    }
}

public sealed class IntegrationAttemptConfiguration : IEntityTypeConfiguration<IntegrationAttempt>
{
    public void Configure(EntityTypeBuilder<IntegrationAttempt> b)
    {
        b.HasKey(x => x.Id);
        b.HasOne<IntegrationOperation>().WithMany().HasForeignKey(x => x.OperationId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.OperationId, x.Number }).IsUnique();
        b.HasIndex(x => x.OperationId).IsUnique().HasFilter("\"StatusCode\" = 'started'")
            .HasDatabaseName("UX_IntegrationAttempts_Active");
        b.Property(x => x.StatusCode).HasMaxLength(24).IsRequired();
        b.ToTable("IntegrationAttempts", t =>
        {
            t.HasCheckConstraint("CK_IntegrationAttempts_Number", "\"Number\" > 0");
            t.HasCheckConstraint("CK_IntegrationAttempts_Status", "\"StatusCode\" IN ('started','acknowledged','failed','unknown')");
            t.HasCheckConstraint("CK_IntegrationAttempts_Completion", """
                ("StatusCode" = 'started') = ("FinishedAt" IS NULL)
                AND ("FinishedAt" IS NULL OR "FinishedAt" >= "StartedAt")
                """);
        });
    }
}

public sealed class IntegrationReceiptConfiguration : IEntityTypeConfiguration<IntegrationReceipt>
{
    public void Configure(EntityTypeBuilder<IntegrationReceipt> b)
    {
        b.HasKey(x => x.MessageId);
        b.HasOne<IntegrationOperation>().WithMany().HasForeignKey(x => x.OperationId).OnDelete(DeleteBehavior.Restrict);
        // The first probe expects exactly one terminal result, independently of delivery retries.
        b.HasIndex(x => x.OperationId).IsUnique();
        b.HasIndex(x => new { x.ReceivedAt, x.MessageId }).HasFilter("\"ProcessedAt\" IS NULL");
        b.Property(x => x.CanonicalContent).HasColumnType("text").IsRequired();
        b.Property(x => x.ContentSha256).HasMaxLength(64).IsRequired();
        b.ToTable("IntegrationReceipts", t =>
        {
            t.HasCheckConstraint("CK_IntegrationReceipts_Content", $"octet_length(\"CanonicalContent\") BETWEEN 1 AND {IntegrationContracts.MaxEnvelopeBytes}");
            t.HasCheckConstraint("CK_IntegrationReceipts_Hash", "\"ContentSha256\" ~ '^[0-9a-f]{64}$'");
            t.HasCheckConstraint("CK_IntegrationReceipts_Dates", "\"ProcessedAt\" IS NULL OR \"ProcessedAt\" >= \"ReceivedAt\"");
            t.HasCheckConstraint("CK_IntegrationReceipts_Id", "\"MessageId\" <> '00000000-0000-0000-0000-000000000000'::uuid");
        });
    }
}
