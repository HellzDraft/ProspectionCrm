using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ProspectionCrm.Api.Entities;

namespace ProspectionCrm.Api.Data.Configurations;

public class ProjectConfiguration : IEntityTypeConfiguration<Project>
{
    public void Configure(EntityTypeBuilder<Project> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Name).IsRequired().HasMaxLength(200);
        builder.Property(x => x.Description).HasMaxLength(10000);
        builder.Property(x => x.Role).HasMaxLength(200);
        builder.Property(x => x.RepositoryUrl).HasMaxLength(2048);
        builder.Property(x => x.WebsiteUrl).HasMaxLength(2048);
        builder.Property(x => x.StartedOn).HasColumnType("date");
        builder.Property(x => x.EndedOn).HasColumnType("date");
        builder.HasOne(x => x.Workspace).WithMany(x => x.Projects)
            .HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => new { x.WorkspaceId, x.ArchivedAt });
        builder.HasIndex(x => new { x.WorkspaceId, x.Name });
        builder.ToTable("Projects", table =>
        {
            table.HasCheckConstraint("CK_Projects_EndedOn", "\"EndedOn\" IS NULL OR \"StartedOn\" IS NULL OR \"EndedOn\" >= \"StartedOn\"");
        });
    }
}
