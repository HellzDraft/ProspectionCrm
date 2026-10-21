using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ProspectionCrm.Api.Entities;

namespace ProspectionCrm.Api.Data.Configurations;

public sealed class CalendarEventPlanConfiguration : IEntityTypeConfiguration<CalendarEventPlan>
{
    public void Configure(EntityTypeBuilder<CalendarEventPlan> b)
    {
        b.HasKey(x => x.Id);
        b.HasOne<Workspace>().WithMany().HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<CalendarAccount>().WithMany().HasForeignKey(x => new { x.WorkspaceId, x.CalendarAccountId, x.ConnectionId })
            .HasPrincipalKey(x => new { x.WorkspaceId, x.Id, x.ConnectionId }).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<AutomationActionRequest>().WithOne().HasForeignKey<CalendarEventPlan>(x => new { x.WorkspaceId, x.AutomationActionRequestId })
            .HasPrincipalKey<AutomationActionRequest>(x => new { x.WorkspaceId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.AutomationActionRequestId).IsUnique();
        b.HasOne<Opportunity>().WithMany().HasForeignKey(x => new { x.WorkspaceId, x.OpportunityId })
            .HasPrincipalKey(x => new { x.WorkspaceId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Company>().WithMany().HasForeignKey(x => new { x.WorkspaceId, x.CompanyId })
            .HasPrincipalKey(x => new { x.WorkspaceId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Contact>().WithMany().HasForeignKey(x => new { x.WorkspaceId, x.ContactId })
            .HasPrincipalKey(x => new { x.WorkspaceId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.WorkspaceId, x.CreatedAt, x.Id });
        b.Property(x => x.StatusCode).HasMaxLength(24);
        b.HasOne<Calendar>().WithMany().HasForeignKey(x => new { x.WorkspaceId, x.CalendarId, x.CalendarAccountId, x.ConnectionId })
            .HasPrincipalKey(x => new { x.WorkspaceId, x.Id, x.CalendarAccountId, x.ConnectionId }).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<CalendarEvent>().WithMany().HasForeignKey(x => new { x.WorkspaceId, x.TargetCalendarEventId })
            .HasPrincipalKey(x => new { x.WorkspaceId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.Property(x => x.OperationCode).HasMaxLength(24);
        b.Property(x => x.TargetExternalEventId).HasMaxLength(500);
        b.Property(x => x.ContentHash).HasMaxLength(64);
        b.Property(x => x.ContentJson).HasColumnType("jsonb");
        b.Property(x => x.TargetSnapshotJson).HasColumnType("jsonb");
        b.Property(x => x.Revision).IsConcurrencyToken();
        b.ToTable("CalendarEventPlans", t =>
        {
            t.HasCheckConstraint("CK_CalendarEventPlans_Hash", "\"ContentHash\" ~ '^[0-9a-f]{64}$'");
            t.HasCheckConstraint("CK_CalendarEventPlans_Revision", "\"Revision\" >= 0 AND (\"SubmittedRevision\" IS NULL OR (\"SubmittedRevision\" >= 0 AND \"SubmittedRevision\" < \"Revision\"))");
            t.HasCheckConstraint("CK_CalendarEventPlans_Content", """
                jsonb_typeof("ContentJson") = 'object' AND octet_length("ContentJson"::text) <= 60000
                AND (("OperationCode" = 'create' AND "TargetCalendarEventId" IS NULL AND "TargetExternalEventId" IS NULL
                    AND "TargetExternalRevision" IS NULL AND "TargetSnapshotJson" IS NULL)
                OR ("OperationCode" = 'update' AND "TargetCalendarEventId" IS NOT NULL AND "TargetExternalEventId" IS NOT NULL
                    AND "TargetExternalRevision" IS NOT NULL AND "TargetExternalRevision" >= 0
                    AND "TargetSnapshotJson" IS NOT NULL AND jsonb_typeof("TargetSnapshotJson") = 'object'))
                """);
            t.HasCheckConstraint("CK_CalendarEventPlans_State", """
                ("StatusCode" = 'draft' AND "AutomationActionRequestId" IS NULL AND "SubmittedAt" IS NULL AND "SubmittedRevision" IS NULL
                    AND "ApprovedAt" IS NULL AND "RejectedAt" IS NULL AND "CancelledAt" IS NULL)
                OR ("StatusCode" IN ('pending-approval','approved','rejected') AND "AutomationActionRequestId" IS NOT NULL
                    AND "SubmittedAt" IS NOT NULL AND "SubmittedRevision" IS NOT NULL AND "CancelledAt" IS NULL
                    AND (("StatusCode" = 'pending-approval' AND "ApprovedAt" IS NULL AND "RejectedAt" IS NULL)
                        OR ("StatusCode" = 'approved' AND "ApprovedAt" IS NOT NULL AND "RejectedAt" IS NULL)
                        OR ("StatusCode" = 'rejected' AND "RejectedAt" IS NOT NULL AND "ApprovedAt" IS NULL)))
                OR ("StatusCode" = 'cancelled' AND "CancelledAt" IS NOT NULL AND "ApprovedAt" IS NULL AND "RejectedAt" IS NULL
                    AND (("AutomationActionRequestId" IS NULL AND "SubmittedRevision" IS NULL AND "SubmittedAt" IS NULL)
                        OR ("AutomationActionRequestId" IS NOT NULL AND "SubmittedRevision" IS NOT NULL AND "SubmittedAt" IS NOT NULL)))
                """);
            t.HasCheckConstraint("CK_CalendarEventPlans_Dates", """
                ("UpdatedAt" IS NULL OR "UpdatedAt" >= "CreatedAt") AND ("SubmittedAt" IS NULL OR "SubmittedAt" >= "CreatedAt")
                AND ("ApprovedAt" IS NULL OR "ApprovedAt" >= "SubmittedAt") AND ("RejectedAt" IS NULL OR "RejectedAt" >= "SubmittedAt")
                AND ("CancelledAt" IS NULL OR "CancelledAt" >= COALESCE("SubmittedAt", "CreatedAt"))
                """);
        });
    }
}
