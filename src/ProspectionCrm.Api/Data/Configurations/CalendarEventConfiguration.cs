using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ProspectionCrm.Api.Entities;

namespace ProspectionCrm.Api.Data.Configurations;

public class CalendarEventConfiguration : IEntityTypeConfiguration<CalendarEvent>
{
    public void Configure(EntityTypeBuilder<CalendarEvent> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.ProviderCode).IsRequired().HasMaxLength(50);
        builder.Property(x => x.ExternalEventId).HasMaxLength(500);
        builder.Property(x => x.Title).IsRequired().HasMaxLength(500);
        builder.Property(x => x.Description).HasMaxLength(10000);
        builder.Property(x => x.Location).HasMaxLength(500);
        builder.Property(x => x.TimeZoneId).HasMaxLength(100);
        builder.HasOne(x => x.Workspace).WithMany(x => x.CalendarEvents)
            .HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.Opportunity).WithMany(x => x.CalendarEvents)
            .HasForeignKey(x => x.OpportunityId).OnDelete(DeleteBehavior.SetNull);
        builder.HasOne(x => x.Contact).WithMany(x => x.CalendarEvents)
            .HasForeignKey(x => x.ContactId).OnDelete(DeleteBehavior.SetNull);
        builder.HasIndex(x => new { x.WorkspaceId, x.StartsAt });
        builder.HasIndex(x => x.OpportunityId);
        builder.HasIndex(x => x.ContactId);
        builder.HasIndex(x => new { x.WorkspaceId, x.ProviderCode, x.ExternalEventId }).IsUnique()
            .HasDatabaseName("UX_CalendarEvents_Workspace_Provider_ExternalEvent")
            .HasFilter("\"ExternalEventId\" IS NOT NULL AND \"CalendarId\" IS NULL");
        builder.HasOne<Calendar>().WithMany().HasForeignKey(x => new { x.WorkspaceId, x.CalendarId, x.ProviderCode })
            .HasPrincipalKey(x => new { x.WorkspaceId, x.Id, x.ProviderCode }).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => new { x.CalendarId, x.ExternalEventId }).IsUnique()
            .HasFilter("\"CalendarId\" IS NOT NULL");
        builder.HasIndex(x => new { x.CalendarId, x.SeriesId, x.OccurrenceId }).IsUnique()
            .HasFilter("\"CalendarId\" IS NOT NULL AND \"SeriesId\" IS NOT NULL");
        builder.Property(x => x.ExternalContentSha256).HasMaxLength(64);
        builder.Property(x => x.StatusCode).HasMaxLength(24);
        builder.Property(x => x.SeriesId).HasMaxLength(500);
        builder.Property(x => x.OccurrenceId).HasMaxLength(500);
        builder.ToTable("CalendarEvents", table =>
        {
            table.HasCheckConstraint("CK_CalendarEvents_Imported", """
                "CalendarId" IS NULL OR ("ExternalEventId" IS NOT NULL AND length(btrim("ExternalEventId")) > 0
                AND "ExternalRevision" IS NOT NULL AND "ExternalRevision" >= 0
                AND "ExternalContentSha256" IS NOT NULL AND "ExternalContentSha256" ~ '^[a-f0-9]{64}$'
                AND "StatusCode" IS NOT NULL AND "StatusCode" IN ('confirmed','cancelled')
                AND "EndsAt" > "StartsAt" AND (("SeriesId" IS NULL AND "OccurrenceId" IS NULL)
                OR ("SeriesId" IS NOT NULL AND "OccurrenceId" IS NOT NULL)))
                """);
            table.HasCheckConstraint("CK_CalendarEvents_EndsAt", "\"EndsAt\" >= \"StartsAt\"");
        });
    }
}
