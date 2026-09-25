using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ProspectionCrm.Api.Entities;

namespace ProspectionCrm.Api.Data.Configurations;

public class AiModelConfigurationConfiguration : IEntityTypeConfiguration<AiModelConfiguration>
{
    public void Configure(EntityTypeBuilder<AiModelConfiguration> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Name).IsRequired().HasMaxLength(200);
        builder.Property(x => x.ProviderCode).IsRequired().HasMaxLength(50);
        builder.Property(x => x.ModelName).IsRequired().HasMaxLength(200);
        builder.HasOne(x => x.Workspace).WithMany(x => x.AiModelConfigurations)
            .HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => x.WorkspaceId).IsUnique()
            .HasFilter("\"IsDefault\" = true AND \"ArchivedAt\" IS NULL")
            .HasDatabaseName("UX_AiModelConfigurations_ActiveDefault");
        builder.HasIndex(x => new { x.WorkspaceId, x.ArchivedAt });
        builder.HasIndex(x => new { x.WorkspaceId, x.Enabled });
        builder.HasIndex(x => new { x.WorkspaceId, x.ProviderCode });
        builder.HasIndex(x => new { x.WorkspaceId, x.ProviderCode, x.ModelName });
    }
}
