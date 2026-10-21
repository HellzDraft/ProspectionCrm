using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ProspectionCrm.Api.Entities;

namespace ProspectionCrm.Api.Data.Configurations;

public sealed class CalendarAccountConfiguration : IEntityTypeConfiguration<CalendarAccount>
{
    public void Configure(EntityTypeBuilder<CalendarAccount> b)
    {
        b.HasKey(x => x.Id);
        b.HasAlternateKey(x => new { x.WorkspaceId, x.Id });
        b.HasAlternateKey(x => new { x.WorkspaceId, x.Id, x.ConnectionId });
        b.HasOne<Workspace>().WithMany().HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
        b.Property(x => x.ProviderCode).HasMaxLength(50);
        b.Property(x => x.ExternalAccountId).HasMaxLength(500);
        b.Property(x => x.DisplayName).HasMaxLength(200);
        b.Property(x => x.StatusCode).HasMaxLength(24);
        b.Property(x => x.Revision).IsConcurrencyToken();
        b.HasIndex(x => new { x.WorkspaceId, x.ProviderCode, x.ExternalAccountId }).IsUnique();
        b.ToTable("CalendarAccounts", t =>
        {
            t.HasCheckConstraint("CK_CalendarAccounts_Status", "\"StatusCode\" IN ('active','disabled','revoked')");
            t.HasCheckConstraint("CK_CalendarAccounts_Identity", """
                "Id" <> '00000000-0000-0000-0000-000000000000'::uuid
                AND "ConnectionId" <> '00000000-0000-0000-0000-000000000000'::uuid
                AND "ProviderCode" ~ '^[a-z][a-z0-9-]{0,49}$'
                AND length(btrim("ExternalAccountId")) > 0
                """);
            t.HasCheckConstraint("CK_CalendarAccounts_Revision", "\"Revision\" >= 0");
        });
    }
}

public sealed class CalendarSyncStateConfiguration : IEntityTypeConfiguration<CalendarSyncState>
{
    public void Configure(EntityTypeBuilder<CalendarSyncState> b)
    {
        b.HasKey(x => x.CalendarId);
        b.HasOne<Calendar>().WithOne().HasForeignKey<CalendarSyncState>(x => new { x.WorkspaceId, x.CalendarId })
            .HasPrincipalKey<Calendar>(x => new { x.WorkspaceId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.Property(x => x.Cursor).HasMaxLength(2048);
        b.Property(x => x.Revision).IsConcurrencyToken();
        b.ToTable("CalendarSyncStates", t => t.HasCheckConstraint("CK_CalendarSyncStates_Revision", "\"Revision\" >= 0"));
    }
}

public sealed class CalendarSyncJobConfiguration : IEntityTypeConfiguration<CalendarSyncJob>
{
    public void Configure(EntityTypeBuilder<CalendarSyncJob> b)
    {
        b.HasKey(x => x.Id);
        b.HasOne<Calendar>().WithMany().HasForeignKey(x => new { x.WorkspaceId, x.CalendarId, x.ConnectionId })
            .HasPrincipalKey(x => new { x.WorkspaceId, x.Id, x.ConnectionId }).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<IntegrationOperation>().WithMany().HasForeignKey(x => x.IntegrationOperationId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.IntegrationOperationId).IsUnique();
        b.HasIndex(x => new { x.WorkspaceId, x.EnqueuedAt });
        b.HasIndex(x => x.CalendarId).IsUnique().HasDatabaseName("UX_CalendarSyncJobs_ActiveCalendar")
            .HasFilter("\"StatusCode\" IN ('queued','running','awaiting-result','dispatch-unknown','result-received')");
        b.Property(x => x.StatusCode).HasMaxLength(24);
        b.Property(x => x.TriggerTypeCode).HasMaxLength(24);
        b.Property(x => x.OperationType).HasMaxLength(50);
        b.Property(x => x.ErrorCode).HasMaxLength(64);
        b.Property(x => x.ConflictingExternalEventId).HasMaxLength(500);
        b.Property(x => x.PreviousCursor).HasMaxLength(2048);
        b.Property(x => x.NextCursor).HasMaxLength(2048);
        // The migration also binds workspace/connection/type to the operation's unique index,
        // preserving nullable connections on historical operations (as in Phase941).
        b.ToTable("CalendarSyncJobs", t =>
        {
            t.HasCheckConstraint("CK_CalendarSyncJobs_Contract", "\"OperationType\" = 'calendar.sync' AND \"TriggerTypeCode\" = 'manual' AND \"MaxItems\" BETWEEN 1 AND 100");
            t.HasCheckConstraint("CK_CalendarSyncJobs_Revision", "\"StateRevision\" >= 0 AND \"AccountRevision\" >= 0 AND \"CalendarRevision\" >= 0");
            t.HasCheckConstraint("CK_CalendarSyncJobs_Counts", "\"UpdatedCount\" >= 0 AND \"CreatedCount\" >= 0 AND \"ExistingCount\" >= 0 AND \"UpdatedCount\" + \"CreatedCount\" + \"ExistingCount\" <= \"MaxItems\"");
            t.HasCheckConstraint("CK_CalendarSyncJobs_State", """
                ("StatusCode" = 'queued' AND "StartedAt" IS NULL AND "FinishedAt" IS NULL AND "ErrorCode" IS NULL)
                OR ("StatusCode" IN ('running','awaiting-result','result-received') AND "StartedAt" IS NOT NULL AND "FinishedAt" IS NULL AND "ErrorCode" IS NULL)
                OR ("StatusCode" = 'dispatch-unknown' AND "StartedAt" IS NOT NULL AND "FinishedAt" IS NULL AND "ErrorCode" IS NOT NULL AND "ErrorCode" = 'CalendarDispatchUnknown')
                OR ("StatusCode" = 'succeeded' AND "StartedAt" IS NOT NULL AND "FinishedAt" IS NOT NULL AND "ErrorCode" IS NULL)
                OR ("StatusCode" IN ('failed','cancelled') AND "FinishedAt" IS NOT NULL AND "ErrorCode" IS NOT NULL)
                """);
            t.HasCheckConstraint("CK_CalendarSyncJobs_Dates", """
                ("StartedAt" IS NULL OR "StartedAt" >= "EnqueuedAt")
                AND ("FinishedAt" IS NULL OR "FinishedAt" >= COALESCE("StartedAt", "EnqueuedAt"))
                """);
        });
    }
}

public sealed class CalendarConfiguration : IEntityTypeConfiguration<Calendar>
{
    public void Configure(EntityTypeBuilder<Calendar> b)
    {
        b.HasKey(x => x.Id);
        b.HasAlternateKey(x => new { x.WorkspaceId, x.Id, x.CalendarAccountId, x.ConnectionId });
        b.HasAlternateKey(x => new { x.WorkspaceId, x.Id });
        b.HasAlternateKey(x => new { x.WorkspaceId, x.Id, x.ConnectionId });
        b.HasAlternateKey(x => new { x.WorkspaceId, x.Id, x.ProviderCode });
        b.HasOne<CalendarAccount>().WithMany().HasForeignKey(x => new { x.WorkspaceId, x.CalendarAccountId, x.ConnectionId, x.ProviderCode })
            .HasPrincipalKey(x => new { x.WorkspaceId, x.Id, x.ConnectionId, x.ProviderCode }).OnDelete(DeleteBehavior.Restrict);
        b.Property(x => x.ProviderCode).HasMaxLength(50);
        b.Property(x => x.ExternalCalendarId).HasMaxLength(500);
        b.Property(x => x.DisplayName).HasMaxLength(200);
        b.Property(x => x.TimeZoneId).HasMaxLength(100);
        b.Property(x => x.StatusCode).HasMaxLength(24);
        b.Property(x => x.Revision).IsConcurrencyToken();
        b.HasIndex(x => new { x.CalendarAccountId, x.ExternalCalendarId }).IsUnique();
        b.ToTable("Calendars", t =>
        {
            t.HasCheckConstraint("CK_Calendars_Status", "\"StatusCode\" IN ('active','disabled')");
            t.HasCheckConstraint("CK_Calendars_Identity", "length(btrim(\"ExternalCalendarId\")) > 0 AND \"Revision\" >= 0");
        });
    }
}
