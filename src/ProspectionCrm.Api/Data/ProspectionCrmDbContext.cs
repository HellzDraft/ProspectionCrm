using Microsoft.EntityFrameworkCore;
using ProspectionCrm.Api.Entities;

namespace ProspectionCrm.Api.Data;

public class ProspectionCrmDbContext(DbContextOptions<ProspectionCrmDbContext> options)
    : DbContext(options)
{
    public DbSet<Company> Companies => Set<Company>();
    public DbSet<Contact> Contacts => Set<Contact>();
    public DbSet<Opportunity> Opportunities => Set<Opportunity>();
    public DbSet<CrmTask> CrmTasks => Set<CrmTask>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var company = modelBuilder.Entity<Company>();
        company.HasKey(x => x.Id);
        company.Property(x => x.Name).IsRequired().HasMaxLength(200);
        company.Property(x => x.Website).HasMaxLength(2048);
        company.Property(x => x.Location).HasMaxLength(200);
        company.HasIndex(x => x.Name);

        var contact = modelBuilder.Entity<Contact>();
        contact.HasKey(x => x.Id);
        contact.Property(x => x.FirstName).IsRequired().HasMaxLength(100);
        contact.Property(x => x.LastName).IsRequired().HasMaxLength(100);
        contact.Property(x => x.Email).HasMaxLength(254);
        contact.Property(x => x.Phone).HasMaxLength(50);
        contact.Property(x => x.JobTitle).HasMaxLength(200);
        contact.Property(x => x.LinkedInUrl).HasMaxLength(2048);
        contact.HasOne(x => x.Company).WithMany(x => x.Contacts)
            .HasForeignKey(x => x.CompanyId).OnDelete(DeleteBehavior.SetNull);
        contact.HasIndex(x => x.CompanyId);
        contact.HasIndex(x => x.Email);

        var opportunity = modelBuilder.Entity<Opportunity>();
        opportunity.HasKey(x => x.Id);
        opportunity.Property(x => x.Title).IsRequired().HasMaxLength(200);
        opportunity.Property(x => x.PipelineCode).IsRequired().HasMaxLength(50);
        opportunity.Property(x => x.StatusCode).IsRequired().HasMaxLength(50);
        opportunity.Property(x => x.PriorityCode).IsRequired().HasMaxLength(50);
        opportunity.Property(x => x.Location).HasMaxLength(200);
        opportunity.Property(x => x.SourceName).HasMaxLength(200);
        opportunity.Property(x => x.SourceUrl).HasMaxLength(2048);
        opportunity.Property(x => x.Notes).HasMaxLength(10000);
        opportunity.HasOne(x => x.Company).WithMany(x => x.Opportunities)
            .HasForeignKey(x => x.CompanyId).OnDelete(DeleteBehavior.SetNull);
        opportunity.HasOne(x => x.Contact).WithMany(x => x.Opportunities)
            .HasForeignKey(x => x.ContactId).OnDelete(DeleteBehavior.SetNull);
        opportunity.HasIndex(x => x.CompanyId);
        opportunity.HasIndex(x => x.ContactId);
        opportunity.HasIndex(x => x.StatusCode);
        opportunity.HasIndex(x => x.SourceUrl);
        opportunity.HasIndex(x => x.FollowUpDueAt);

        var crmTask = modelBuilder.Entity<CrmTask>();
        crmTask.HasKey(x => x.Id);
        crmTask.Property(x => x.Title).IsRequired().HasMaxLength(200);
        crmTask.Property(x => x.Description).HasMaxLength(10000);
        crmTask.HasOne(x => x.Opportunity).WithMany(x => x.CrmTasks)
            .HasForeignKey(x => x.OpportunityId).IsRequired().OnDelete(DeleteBehavior.Cascade);
        crmTask.HasIndex(x => x.OpportunityId);
        crmTask.HasIndex(x => x.DueAt);
        crmTask.HasIndex(x => x.IsCompleted);
    }
}
