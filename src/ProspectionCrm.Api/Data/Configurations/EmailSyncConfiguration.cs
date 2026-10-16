using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ProspectionCrm.Api.Entities;

namespace ProspectionCrm.Api.Data.Configurations;

public sealed class EmailAccountConfiguration : IEntityTypeConfiguration<EmailAccount>
{
    public void Configure(EntityTypeBuilder<EmailAccount> b)
    {
        b.HasKey(x => x.Id);
        b.HasAlternateKey(x => new { x.WorkspaceId, x.Id });
        b.HasAlternateKey(x => new { x.WorkspaceId, x.Id, x.ConnectionId });
        b.HasOne<Workspace>().WithMany().HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
        b.Property(x => x.ProviderCode).HasMaxLength(50);
        b.Property(x => x.ExternalAccountId).HasMaxLength(500);
        b.Property(x => x.EmailAddress).HasMaxLength(320);
        b.Property(x => x.DisplayName).HasMaxLength(200);
        b.Property(x => x.StatusCode).HasMaxLength(24);
        b.Property(x => x.Revision).IsConcurrencyToken();
        b.HasIndex(x => new { x.WorkspaceId, x.ProviderCode, x.ExternalAccountId }).IsUnique();
        b.ToTable("EmailAccounts", t =>
        {
            t.HasCheckConstraint("CK_EmailAccounts_Status", "\"StatusCode\" IN ('active','disabled','revoked')");
            t.HasCheckConstraint("CK_EmailAccounts_Identity", """
                "Id" <> '00000000-0000-0000-0000-000000000000'::uuid
                AND "ConnectionId" <> '00000000-0000-0000-0000-000000000000'::uuid
                AND "ProviderCode" ~ '^[a-z][a-z0-9-]{0,49}$'
                AND length(btrim("ExternalAccountId")) > 0 AND length(btrim("EmailAddress")) > 0
                """);
            t.HasCheckConstraint("CK_EmailAccounts_Revision", "\"Revision\" >= 0");
        });
    }
}

public sealed class EmailSyncStateConfiguration : IEntityTypeConfiguration<EmailSyncState>
{
    public void Configure(EntityTypeBuilder<EmailSyncState> b)
    {
        b.HasKey(x => x.EmailAccountId);
        b.HasOne<EmailAccount>().WithOne().HasForeignKey<EmailSyncState>(x => new { x.WorkspaceId, x.EmailAccountId })
            .HasPrincipalKey<EmailAccount>(x => new { x.WorkspaceId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.Property(x => x.Cursor).HasMaxLength(2048);
        b.Property(x => x.Revision).IsConcurrencyToken();
        b.ToTable("EmailSyncStates", t => t.HasCheckConstraint("CK_EmailSyncStates_Revision", "\"Revision\" >= 0"));
    }
}

public sealed class EmailSyncJobConfiguration : IEntityTypeConfiguration<EmailSyncJob>
{
    public void Configure(EntityTypeBuilder<EmailSyncJob> b)
    {
        b.HasKey(x => x.Id);
        b.HasOne<EmailAccount>().WithMany().HasForeignKey(x => new { x.WorkspaceId, x.EmailAccountId, x.ConnectionId })
            .HasPrincipalKey(x => new { x.WorkspaceId, x.Id, x.ConnectionId }).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<IntegrationOperation>().WithMany().HasForeignKey(x => x.IntegrationOperationId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.IntegrationOperationId).IsUnique();
        b.HasIndex(x => new { x.WorkspaceId, x.EnqueuedAt });
        b.HasIndex(x => x.EmailAccountId).IsUnique().HasDatabaseName("UX_EmailSyncJobs_ActiveAccount")
            .HasFilter("\"StatusCode\" IN ('queued','running','awaiting-result','dispatch-unknown','result-received')");
        b.Property(x => x.StatusCode).HasMaxLength(24);
        b.Property(x => x.TriggerTypeCode).HasMaxLength(24);
        b.Property(x => x.OperationType).HasMaxLength(50);
        b.Property(x => x.ErrorCode).HasMaxLength(64);
        b.Property(x => x.ConflictingExternalMessageId).HasMaxLength(500);
        b.Property(x => x.PreviousCursor).HasMaxLength(2048);
        b.Property(x => x.NextCursor).HasMaxLength(2048);
        // The migration also binds workspace/connection/type to the operation's unique index,
        // preserving nullable connections on historical operations (as in Phase941).
        b.ToTable("EmailSyncJobs", t =>
        {
            t.HasCheckConstraint("CK_EmailSyncJobs_Contract", "\"OperationType\" = 'email.sync' AND \"TriggerTypeCode\" = 'manual' AND \"MaxItems\" BETWEEN 1 AND 100");
            t.HasCheckConstraint("CK_EmailSyncJobs_Revision", "\"StateRevision\" >= 0 AND \"AccountRevision\" >= 0");
            t.HasCheckConstraint("CK_EmailSyncJobs_Counts", "\"CreatedCount\" >= 0 AND \"ExistingCount\" >= 0 AND \"CreatedCount\" + \"ExistingCount\" <= \"MaxItems\"");
            t.HasCheckConstraint("CK_EmailSyncJobs_State", """
                ("StatusCode" = 'queued' AND "StartedAt" IS NULL AND "FinishedAt" IS NULL AND "ErrorCode" IS NULL)
                OR ("StatusCode" IN ('running','awaiting-result','result-received') AND "StartedAt" IS NOT NULL AND "FinishedAt" IS NULL AND "ErrorCode" IS NULL)
                OR ("StatusCode" = 'dispatch-unknown' AND "StartedAt" IS NOT NULL AND "FinishedAt" IS NULL AND "ErrorCode" IS NOT NULL AND "ErrorCode" = 'EmailDispatchUnknown')
                OR ("StatusCode" = 'succeeded' AND "StartedAt" IS NOT NULL AND "FinishedAt" IS NOT NULL AND "ErrorCode" IS NULL)
                OR ("StatusCode" IN ('failed','cancelled') AND "FinishedAt" IS NOT NULL AND "ErrorCode" IS NOT NULL)
                """);
            t.HasCheckConstraint("CK_EmailSyncJobs_Dates", """
                ("StartedAt" IS NULL OR "StartedAt" >= "EnqueuedAt")
                AND ("FinishedAt" IS NULL OR "FinishedAt" >= COALESCE("StartedAt", "EnqueuedAt"))
                """);
        });
    }
}
