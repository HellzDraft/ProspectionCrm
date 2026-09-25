using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ProspectionCrm.Api.Entities;

namespace ProspectionCrm.Api.Data.Configurations;

public class ApplicationConfiguration : IEntityTypeConfiguration<Application>
{
    public void Configure(EntityTypeBuilder<Application> builder)
    {
        builder.HasKey(x => x.Id);
        builder.HasOne(x => x.CandidateProfile).WithMany()
            .HasForeignKey(x => x.CandidateProfileId).OnDelete(DeleteBehavior.SetNull);
        builder.HasIndex(x => x.CandidateProfileId);
        builder.HasOne(x => x.CvDocument).WithMany()
            .HasForeignKey(x => x.CvDocumentId).OnDelete(DeleteBehavior.SetNull);
        builder.HasIndex(x => x.CvDocumentId);
        builder.HasOne(x => x.CoverLetterDocument).WithMany()
            .HasForeignKey(x => x.CoverLetterDocumentId).OnDelete(DeleteBehavior.SetNull);
        builder.HasIndex(x => x.CoverLetterDocumentId);
        builder.Property(x => x.StatusCode).IsRequired().HasMaxLength(50);
        builder.Property(x => x.ChannelCode).HasMaxLength(50);
        builder.Property(x => x.Notes).HasMaxLength(10000);
        builder.HasOne(x => x.Opportunity).WithMany(x => x.Applications)
            .HasForeignKey(x => x.OpportunityId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(x => x.OpportunityId);
        builder.HasIndex(x => new { x.OpportunityId, x.StatusCode });
        builder.HasIndex(x => x.SubmittedAt);
        builder.ToTable("Applications", table =>
        {
            table.HasCheckConstraint("CK_Applications_StatusCode", "\"StatusCode\" IN ('draft', 'prepared', 'submitted', 'acknowledged', 'accepted', 'rejected', 'withdrawn')");
        });
    }
}
