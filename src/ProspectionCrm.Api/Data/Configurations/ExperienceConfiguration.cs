using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ProspectionCrm.Api.Entities;

namespace ProspectionCrm.Api.Data.Configurations;

public class ExperienceConfiguration : IEntityTypeConfiguration<Experience>
{
    public void Configure(EntityTypeBuilder<Experience> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Title).IsRequired().HasMaxLength(200);
        builder.Property(x => x.OrganizationName).HasMaxLength(200);
        builder.Property(x => x.Location).HasMaxLength(200);
        builder.Property(x => x.Description).HasMaxLength(10000);
        builder.Property(x => x.StartedOn).HasColumnType("date");
        builder.Property(x => x.EndedOn).HasColumnType("date");
        builder.HasOne(x => x.Workspace).WithMany(x => x.Experiences)
            .HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => x.WorkspaceId);
        builder.ToTable("Experiences", table =>
        {
            table.HasCheckConstraint("CK_Experiences_EndedOn", "\"EndedOn\" IS NULL OR \"StartedOn\" IS NULL OR \"EndedOn\" >= \"StartedOn\"");
        });
    }
}
