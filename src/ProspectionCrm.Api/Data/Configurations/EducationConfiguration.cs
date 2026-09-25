using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ProspectionCrm.Api.Entities;

namespace ProspectionCrm.Api.Data.Configurations;

public class EducationConfiguration : IEntityTypeConfiguration<Education>
{
    public void Configure(EntityTypeBuilder<Education> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.InstitutionName).IsRequired().HasMaxLength(200);
        builder.Property(x => x.Degree).HasMaxLength(200);
        builder.Property(x => x.FieldOfStudy).HasMaxLength(200);
        builder.Property(x => x.Description).HasMaxLength(10000);
        builder.Property(x => x.StartedOn).HasColumnType("date");
        builder.Property(x => x.EndedOn).HasColumnType("date");
        builder.HasOne(x => x.Workspace).WithMany(x => x.Educations)
            .HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
        builder.ToTable("Educations", table =>
        {
            table.HasCheckConstraint("CK_Educations_EndedOn", "\"EndedOn\" IS NULL OR \"StartedOn\" IS NULL OR \"EndedOn\" >= \"StartedOn\"");
        });
    }
}
