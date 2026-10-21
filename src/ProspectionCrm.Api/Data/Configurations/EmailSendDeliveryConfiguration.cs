using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ProspectionCrm.Api.Entities;

namespace ProspectionCrm.Api.Data.Configurations;

public sealed class EmailSendDeliveryConfiguration : IEntityTypeConfiguration<EmailSendDelivery>
{
    public void Configure(EntityTypeBuilder<EmailSendDelivery> b)
    {
        b.HasKey(x => x.Id);
        b.HasIndex(x => x.EmailSendPlanId).IsUnique();
        b.HasIndex(x => x.AutomationActionRequestId).IsUnique();
        b.HasIndex(x => x.IntegrationOperationId).IsUnique();
        b.HasIndex(x => x.IdempotencyKey).IsUnique();
        b.HasIndex(x => new { x.WorkspaceId, x.StatusCode, x.CreatedAt });
        b.HasOne<EmailSendPlan>().WithMany().HasForeignKey(x => new { x.WorkspaceId, x.EmailSendPlanId })
            .HasPrincipalKey(x => new { x.WorkspaceId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<AutomationActionRequest>().WithMany().HasForeignKey(x => new { x.WorkspaceId, x.AutomationActionRequestId })
            .HasPrincipalKey(x => new { x.WorkspaceId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<EmailAccount>().WithMany().HasForeignKey(x => new { x.WorkspaceId, x.EmailAccountId, x.ConnectionId })
            .HasPrincipalKey(x => new { x.WorkspaceId, x.Id, x.ConnectionId }).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<IntegrationOperation>().WithMany().HasForeignKey(x => x.IntegrationOperationId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<EmailMessage>().WithMany().HasForeignKey(x => x.EmailMessageId).OnDelete(DeleteBehavior.Restrict);
        b.Property(x => x.ContentHash).HasMaxLength(64);
        b.Property(x => x.IdempotencyKey).HasMaxLength(64);
        b.Property(x => x.OperationType).HasMaxLength(50);
        b.Property(x => x.ProviderCode).HasMaxLength(50);
        b.Property(x => x.StatusCode).HasMaxLength(24);
        b.Property(x => x.ExternalMessageId).HasMaxLength(500);
        b.Property(x => x.DiagnosticCode).HasMaxLength(64);
        b.ToTable("EmailSendDeliveries", t =>
        {
            t.HasCheckConstraint("CK_EmailSendDeliveries_Status", "\"StatusCode\" IN ('pending','dispatching','awaiting-result','send-unknown','result-received','sent','failed')");
            t.HasCheckConstraint("CK_EmailSendDeliveries_Identity", "\"OperationType\" = 'email.send' AND \"ContentHash\" ~ '^[0-9a-f]{64}$' AND \"IdempotencyKey\" = 'email-send-' || \"Id\"::text");
            t.HasCheckConstraint("CK_EmailSendDeliveries_Proof", "(\"StatusCode\" = 'sent') = (\"EmailMessageId\" IS NOT NULL AND \"ExternalMessageId\" IS NOT NULL) AND (\"StatusCode\" IN ('sent','failed')) = (\"FinishedAt\" IS NOT NULL)");
            t.HasCheckConstraint("CK_EmailSendDeliveries_Dates", "(\"StartedAt\" IS NULL OR \"StartedAt\" >= \"CreatedAt\") AND (\"FinishedAt\" IS NULL OR \"FinishedAt\" >= \"CreatedAt\")");
        });
    }
}
