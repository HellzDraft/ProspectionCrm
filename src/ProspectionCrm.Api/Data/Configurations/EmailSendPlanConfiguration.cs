using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ProspectionCrm.Api.Entities;

namespace ProspectionCrm.Api.Data.Configurations;

public sealed class EmailSendPlanConfiguration : IEntityTypeConfiguration<EmailSendPlan>
{
    public void Configure(EntityTypeBuilder<EmailSendPlan> b)
    {
        b.HasKey(x => x.Id);
        b.HasOne<Workspace>().WithMany().HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<EmailAccount>().WithMany().HasForeignKey(x => new { x.WorkspaceId, x.EmailAccountId, x.ConnectionId })
            .HasPrincipalKey(x => new { x.WorkspaceId, x.Id, x.ConnectionId }).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<AutomationActionRequest>().WithOne().HasForeignKey<EmailSendPlan>(x => new { x.WorkspaceId, x.AutomationActionRequestId })
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
        b.Property(x => x.FromAddressSnapshot).HasMaxLength(320);
        b.Property(x => x.Subject).HasMaxLength(1000);
        b.Property(x => x.BodyText).HasMaxLength(32000);
        b.Property(x => x.ContentHash).HasMaxLength(64);
        b.Property(x => x.ToAddressesJson).HasColumnType("jsonb");
        b.Property(x => x.CcAddressesJson).HasColumnType("jsonb");
        b.Property(x => x.Revision).IsConcurrencyToken();
        b.ToTable("EmailSendPlans", t =>
        {
            t.HasCheckConstraint("CK_EmailSendPlans_Hash", "\"ContentHash\" ~ '^[0-9a-f]{64}$'");
            t.HasCheckConstraint("CK_EmailSendPlans_Revision", "\"Revision\" >= 0 AND (\"SubmittedRevision\" IS NULL OR (\"SubmittedRevision\" >= 0 AND \"SubmittedRevision\" < \"Revision\"))");
            t.HasCheckConstraint("CK_EmailSendPlans_Recipients", """
                jsonb_typeof("ToAddressesJson") = 'array' AND jsonb_typeof("CcAddressesJson") = 'array'
                AND jsonb_array_length("ToAddressesJson") > 0
                AND jsonb_array_length("ToAddressesJson") + jsonb_array_length("CcAddressesJson") <= 100
                """);
            t.HasCheckConstraint("CK_EmailSendPlans_State", """
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
            t.HasCheckConstraint("CK_EmailSendPlans_Dates", """
                ("UpdatedAt" IS NULL OR "UpdatedAt" >= "CreatedAt") AND ("SubmittedAt" IS NULL OR "SubmittedAt" >= "CreatedAt")
                AND ("ApprovedAt" IS NULL OR "ApprovedAt" >= "SubmittedAt") AND ("RejectedAt" IS NULL OR "RejectedAt" >= "SubmittedAt")
                AND ("CancelledAt" IS NULL OR "CancelledAt" >= COALESCE("SubmittedAt", "CreatedAt"))
                """);
        });
    }
}
