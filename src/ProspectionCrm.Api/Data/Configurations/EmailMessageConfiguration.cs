using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ProspectionCrm.Api.Entities;

namespace ProspectionCrm.Api.Data.Configurations;

public class EmailMessageConfiguration : IEntityTypeConfiguration<EmailMessage>
{
    public void Configure(EntityTypeBuilder<EmailMessage> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.ProviderCode).IsRequired().HasMaxLength(50);
        builder.Property(x => x.ExternalMessageId).HasMaxLength(500);
        builder.Property(x => x.ExternalThreadId).HasMaxLength(500);
        builder.Property(x => x.DirectionCode).IsRequired().HasMaxLength(50);
        builder.Property(x => x.FromAddress).IsRequired().HasMaxLength(320);
        builder.Property(x => x.ToAddressesJson).IsRequired().HasColumnType("jsonb");
        builder.Property(x => x.CcAddressesJson).HasColumnType("jsonb");
        builder.Property(x => x.Subject).HasMaxLength(1000);
        builder.Property(x => x.BodyText).HasColumnType("text");
        builder.HasOne(x => x.Workspace).WithMany(x => x.EmailMessages)
            .HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.Opportunity).WithMany(x => x.EmailMessages)
            .HasForeignKey(x => x.OpportunityId).OnDelete(DeleteBehavior.SetNull);
        builder.HasOne(x => x.Company).WithMany(x => x.EmailMessages)
            .HasForeignKey(x => x.CompanyId).OnDelete(DeleteBehavior.SetNull);
        builder.HasOne(x => x.Contact).WithMany(x => x.EmailMessages)
            .HasForeignKey(x => x.ContactId).OnDelete(DeleteBehavior.SetNull);
        builder.HasIndex(x => new { x.WorkspaceId, x.OccurredAt });
        builder.HasIndex(x => new { x.WorkspaceId, x.DirectionCode, x.OccurredAt });
        builder.HasIndex(x => x.OpportunityId);
        builder.HasIndex(x => x.CompanyId);
        builder.HasIndex(x => x.ContactId);
        builder.HasIndex(x => new { x.WorkspaceId, x.ProviderCode, x.ExternalMessageId }).IsUnique()
            .HasDatabaseName("UX_EmailMessages_Workspace_Provider_ExternalMessage")
            .HasFilter("\"ExternalMessageId\" IS NOT NULL");
        builder.ToTable("EmailMessages", table =>
        {
            table.HasCheckConstraint("CK_EmailMessages_DirectionCode", "\"DirectionCode\" IN ('inbound', 'outbound')");
            table.HasCheckConstraint("CK_EmailMessages_ToAddressesJson", "jsonb_typeof(\"ToAddressesJson\") = 'array'");
            table.HasCheckConstraint("CK_EmailMessages_CcAddressesJson", "\"CcAddressesJson\" IS NULL OR jsonb_typeof(\"CcAddressesJson\") = 'array'");
        });
    }
}
