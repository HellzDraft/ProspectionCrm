using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ProspectionCrm.Api.Entities;

namespace ProspectionCrm.Api.Data.Configurations;

public class AiPromptVersionConfiguration : IEntityTypeConfiguration<AiPromptVersion>
{
    public void Configure(EntityTypeBuilder<AiPromptVersion> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.SystemPrompt).HasColumnType("text");
        builder.Property(x => x.UserPromptTemplate).IsRequired().HasColumnType("text");
        builder.Property(x => x.OutputSchemaJson).HasColumnType("jsonb");
        builder.HasOne(x => x.AiPromptTemplate).WithMany(x => x.Versions)
            .HasForeignKey(x => x.AiPromptTemplateId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.DefaultModelConfiguration).WithMany()
            .HasForeignKey(x => x.DefaultModelConfigurationId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => new { x.AiPromptTemplateId, x.VersionNumber }).IsUnique()
            .HasDatabaseName("UX_AiPromptVersions_Template_Version");
        builder.ToTable("AiPromptVersions", table =>
        {
            table.HasCheckConstraint("CK_AiPromptVersions_OutputSchemaJson", "\"OutputSchemaJson\" IS NULL OR jsonb_typeof(\"OutputSchemaJson\") = 'object'");
            table.HasCheckConstraint("CK_AiPromptVersions_VersionNumber", "\"VersionNumber\" > 0");
        });
    }
}
