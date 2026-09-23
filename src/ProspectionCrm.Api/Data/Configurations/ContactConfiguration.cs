using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ProspectionCrm.Api.Entities;

namespace ProspectionCrm.Api.Data.Configurations;

public class ContactConfiguration : IEntityTypeConfiguration<Contact>
{
    public void Configure(EntityTypeBuilder<Contact> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.FirstName).IsRequired().HasMaxLength(100);
        builder.Property(x => x.LastName).IsRequired().HasMaxLength(100);
        builder.Property(x => x.Email).HasMaxLength(254);
        builder.Property(x => x.Phone).HasMaxLength(50);
        builder.Property(x => x.JobTitle).HasMaxLength(200);
        builder.Property(x => x.LinkedInUrl).HasMaxLength(2048);
        builder.HasOne(x => x.Workspace).WithMany(x => x.Contacts)
            .HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.Company).WithMany(x => x.Contacts)
            .HasForeignKey(x => x.CompanyId).OnDelete(DeleteBehavior.SetNull);
        builder.HasIndex(x => new { x.WorkspaceId, x.CompanyId });
        builder.HasIndex(x => new { x.WorkspaceId, x.Email });
    }
}
