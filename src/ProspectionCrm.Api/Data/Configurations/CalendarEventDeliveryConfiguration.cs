using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ProspectionCrm.Api.Entities;

namespace ProspectionCrm.Api.Data.Configurations;

public sealed class CalendarEventDeliveryConfiguration : IEntityTypeConfiguration<CalendarEventDelivery>
{
    public void Configure(EntityTypeBuilder<CalendarEventDelivery> b)
    {
        b.HasKey(x => x.Id);
        b.HasIndex(x => x.CalendarEventPlanId).IsUnique();
        b.HasIndex(x => x.AutomationActionRequestId).IsUnique();
        b.HasIndex(x => x.IntegrationOperationId).IsUnique();
        b.HasIndex(x => x.IdempotencyKey).IsUnique();
        b.HasIndex(x => new { x.WorkspaceId, x.StatusCode, x.CreatedAt });
        b.HasOne<CalendarEventPlan>().WithMany().HasForeignKey(x => new { x.WorkspaceId, x.CalendarEventPlanId })
            .HasPrincipalKey(x => new { x.WorkspaceId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<AutomationActionRequest>().WithMany().HasForeignKey(x => new { x.WorkspaceId, x.AutomationActionRequestId })
            .HasPrincipalKey(x => new { x.WorkspaceId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Calendar>().WithMany().HasForeignKey(x => new { x.WorkspaceId, x.CalendarId, x.CalendarAccountId, x.ConnectionId })
            .HasPrincipalKey(x => new { x.WorkspaceId, x.Id, x.CalendarAccountId, x.ConnectionId }).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<IntegrationOperation>().WithMany().HasForeignKey(x => x.IntegrationOperationId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<CalendarEvent>().WithMany().HasForeignKey(x => new { x.WorkspaceId, x.CalendarEventId })
            .HasPrincipalKey(x => new { x.WorkspaceId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<CalendarEvent>().WithMany().HasForeignKey(x => new { x.WorkspaceId, x.TargetCalendarEventId })
            .HasPrincipalKey(x => new { x.WorkspaceId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.Property(x => x.OperationType).HasMaxLength(50);
        b.Property(x => x.ProviderCode).HasMaxLength(50);
        b.Property(x => x.ExternalCalendarId).HasMaxLength(500);
        b.Property(x => x.ExternalEventId).HasMaxLength(500);
        b.Property(x => x.TargetExternalEventId).HasMaxLength(500);
        b.Property(x => x.ContentHash).HasMaxLength(64);
        b.Property(x => x.TargetContentHash).HasMaxLength(64);
        b.Property(x => x.ProofHash).HasMaxLength(64);
        b.Property(x => x.IdempotencyKey).HasMaxLength(64);
        b.Property(x => x.DiagnosticCode).HasMaxLength(64);
        b.Property(x => x.StatusCode).HasMaxLength(24);
        b.ToTable("CalendarEventDeliveries", t =>
        {
            t.HasCheckConstraint("CK_CalendarDelivery_Identity", "\"ContentHash\" ~ '^[0-9a-f]{64}$' AND \"IdempotencyKey\" = 'calendar-event-' || \"Id\"::text");
            t.HasCheckConstraint("CK_CalendarDelivery_Target", """
                ("OperationType" = 'calendar.event.create' AND "TargetCalendarEventId" IS NULL AND "TargetExternalEventId" IS NULL
                    AND "TargetExternalRevision" IS NULL AND "TargetContentHash" IS NULL)
                OR ("OperationType" = 'calendar.event.update' AND "TargetCalendarEventId" IS NOT NULL AND "TargetExternalEventId" IS NOT NULL
                    AND "TargetExternalRevision" IS NOT NULL AND "TargetExternalRevision" >= 0 AND "TargetContentHash" IS NOT NULL AND "TargetContentHash" ~ '^[0-9a-f]{64}$')
                """);
            t.HasCheckConstraint("CK_CalendarDelivery_State", """
                "StatusCode" IN ('pending','dispatching','awaiting-result','operation-unknown','result-received','succeeded','failed','conflict')
                AND ("StatusCode" = 'pending') = ("StartedAt" IS NULL)
                AND ("StatusCode" IN ('succeeded','failed','conflict')) = ("FinishedAt" IS NOT NULL)
                AND ("StatusCode" <> 'succeeded' OR ("CalendarEventId" IS NOT NULL AND "ExternalEventId" IS NOT NULL AND "ExternalRevision" IS NOT NULL AND "ProofHash" IS NOT NULL))
                AND ("ExternalRevision" IS NULL OR "ExternalRevision" >= 0)
                AND ("ProofHash" IS NULL OR "ProofHash" ~ '^[0-9a-f]{64}$')
                """);
            t.HasCheckConstraint("CK_CalendarDelivery_Dates", """
                ("StartedAt" IS NULL OR "StartedAt" >= "CreatedAt") AND ("FinishedAt" IS NULL OR "FinishedAt" >= "StartedAt")
                """);
        });
    }
}
