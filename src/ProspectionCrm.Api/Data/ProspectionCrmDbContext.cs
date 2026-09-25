using Microsoft.EntityFrameworkCore;
using ProspectionCrm.Api.Entities;

namespace ProspectionCrm.Api.Data;

public class ProspectionCrmDbContext(DbContextOptions<ProspectionCrmDbContext> options)
    : DbContext(options)
{
    public DbSet<UserAccount> UserAccounts => Set<UserAccount>();
    public DbSet<Workspace> Workspaces => Set<Workspace>();
    public DbSet<Company> Companies => Set<Company>();
    public DbSet<Contact> Contacts => Set<Contact>();
    public DbSet<Pipeline> Pipelines => Set<Pipeline>();
    public DbSet<PipelineStage> PipelineStages => Set<PipelineStage>();
    public DbSet<Opportunity> Opportunities => Set<Opportunity>();
    public DbSet<CrmTask> CrmTasks => Set<CrmTask>();

    public DbSet<SourceConfiguration> SourceConfigurations => Set<SourceConfiguration>();
    public DbSet<SavedSearch> SavedSearches => Set<SavedSearch>();
    public DbSet<SourceExecution> SourceExecutions => Set<SourceExecution>();
    public DbSet<OpportunitySource> OpportunitySources => Set<OpportunitySource>();
    public DbSet<Campaign> Campaigns => Set<Campaign>();
    public DbSet<CampaignOpportunity> CampaignOpportunities => Set<CampaignOpportunity>();

    public DbSet<Application> Applications => Set<Application>();
    public DbSet<Proposal> Proposals => Set<Proposal>();
    public DbSet<EmailMessage> EmailMessages => Set<EmailMessage>();
    public DbSet<CalendarEvent> CalendarEvents => Set<CalendarEvent>();

    public DbSet<Document> Documents => Set<Document>();
    public DbSet<CandidateProfile> CandidateProfiles => Set<CandidateProfile>();
    public DbSet<Experience> Experiences => Set<Experience>();
    public DbSet<Education> Educations => Set<Education>();
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<Skill> Skills => Set<Skill>();
    public DbSet<CandidateProfileExperience> CandidateProfileExperiences => Set<CandidateProfileExperience>();
    public DbSet<CandidateProfileEducation> CandidateProfileEducations => Set<CandidateProfileEducation>();
    public DbSet<CandidateProfileProject> CandidateProfileProjects => Set<CandidateProfileProject>();
    public DbSet<CandidateProfileSkill> CandidateProfileSkills => Set<CandidateProfileSkill>();
    public DbSet<ProjectSkill> ProjectSkills => Set<ProjectSkill>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
        => modelBuilder.ApplyConfigurationsFromAssembly(typeof(ProspectionCrmDbContext).Assembly);
}
