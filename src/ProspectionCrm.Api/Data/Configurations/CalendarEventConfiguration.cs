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
            .HasFilter("\"ExternalEventId\" IS NOT NULL");
        builder.ToTable("CalendarEvents", table =>
        {
            table.HasCheckConstraint("CK_CalendarEvents_EndsAt", "\"EndsAt\" >= \"StartsAt\"");
        });
    }
}
