using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ProspectionCrm.Api.Entities;

namespace ProspectionCrm.Api.Data.Configurations;

public class SourceConfigurationConfiguration : IEntityTypeConfiguration<SourceConfiguration>
{
    public void Configure(EntityTypeBuilder<SourceConfiguration> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Name).IsRequired().HasMaxLength(200);
        builder.Property(x => x.SourceTypeCode).IsRequired().HasMaxLength(50);
        builder.Property(x => x.BaseUrl).HasMaxLength(2048);
        builder.Property(x => x.ConfigurationJson).HasColumnType("jsonb");
        builder.HasOne(x => x.Workspace).WithMany(x => x.SourceConfigurations)
            .HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => new { x.WorkspaceId, x.ArchivedAt });
        builder.HasIndex(x => new { x.WorkspaceId, x.SourceTypeCode });
        builder.ToTable("SourceConfigurations", table => table.HasCheckConstraint(
            "CK_SourceConfigurations_ConfigurationJson", "\"ConfigurationJson\" IS NULL OR jsonb_typeof(\"ConfigurationJson\") = 'object'"));
    }
}
