using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ProspectionCrm.Api.Entities;

namespace ProspectionCrm.Api.Data.Configurations;

public class ProjectSkillConfiguration : IEntityTypeConfiguration<ProjectSkill>
{
    public void Configure(EntityTypeBuilder<ProjectSkill> builder)
    {
        builder.HasKey(x => x.Id);
        builder.HasOne(x => x.Project).WithMany(x => x.Skills)
            .HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(x => x.Skill).WithMany()
            .HasForeignKey(x => x.SkillId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => new { x.ProjectId, x.SkillId }).IsUnique();
    }
}
