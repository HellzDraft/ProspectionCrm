using Microsoft.EntityFrameworkCore;
using ProspectionCrm.Api.Data;
using ProspectionCrm.Api.Services;
using ProspectionCrm.Api.Services.Collection;
using ProspectionCrm.Api.Storage;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSerilog((services, configuration) => configuration
    .ReadFrom.Configuration(builder.Configuration)
    .ReadFrom.Services(services)
    .Enrich.WithProperty("Environment", builder.Environment.EnvironmentName));

builder.Services.AddDbContext<ProspectionCrmDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException(
            "Configurez ConnectionStrings:DefaultConnection avec User Secrets ou une variable d'environnement.")));

builder.Services.AddControllers();
builder.Services.AddOptions<FileStorageOptions>()
    .Bind(builder.Configuration.GetRequiredSection(FileStorageOptions.SectionName))
    .Validate(options => !string.IsNullOrWhiteSpace(options.RootPath), "FileStorage:RootPath is required.")
    .ValidateOnStart();
builder.Services.AddSingleton<IFileStorage, LocalFileStorage>();
builder.Services.AddScoped<ICurrentWorkspaceProvider, CurrentWorkspaceProvider>();
builder.Services.AddScoped<IBootstrapService, BootstrapService>();
builder.Services.AddScoped<IInitialPipelineService, InitialPipelineService>();
builder.Services.AddScoped<IPipelineService, PipelineService>();
builder.Services.AddScoped<IPipelineStageService, PipelineStageService>();
builder.Services.AddScoped<IOpportunityService, OpportunityService>();
builder.Services.AddScoped<ICompanyService, CompanyService>();
builder.Services.AddScoped<IContactService, ContactService>();
builder.Services.AddScoped<ICrmTaskService, CrmTaskService>();
builder.Services.AddScoped<ISourceConfigurationService, SourceConfigurationService>();
builder.Services.AddScoped<ISavedSearchService, SavedSearchService>();
builder.Services.AddScoped<ISourceExecutionService, SourceExecutionService>();
builder.Services.AddScoped<IIngestionService, IngestionService>();
builder.Services.AddOptions<RssCollectionOptions>()
    .Bind(builder.Configuration.GetRequiredSection(RssCollectionOptions.SectionName))
    .Validate(options => options.IsValid(), "RssCollection options are outside the supported bounds.")
    .ValidateOnStart();
builder.Services.AddSingleton<IRssDnsResolver, RssDnsResolver>();
builder.Services.AddSingleton<IRssSocketConnector, RssSocketConnector>();
builder.Services.AddSingleton<RssConnectionFactory>();
builder.Services.AddSingleton<IRssFeedTransport, RssFeedTransport>();
builder.Services.AddSingleton<RssAtomFeedParser>();
builder.Services.AddSingleton<ISourceAdapter, RssAtomSourceAdapter>();
builder.Services.AddSingleton<SourceAdapterRegistry>();
builder.Services.AddScoped<ISourceCollectionService, SourceCollectionService>();
builder.Services.AddScoped<IIngestionHistoryReadService, IngestionHistoryReadService>();
builder.Services.AddScoped<IOpportunitySourceService, OpportunitySourceService>();
builder.Services.AddScoped<ICampaignService, CampaignService>();
builder.Services.AddScoped<IApplicationService, ApplicationService>();
builder.Services.AddScoped<IProposalService, ProposalService>();
builder.Services.AddScoped<IEmailMessageService, EmailMessageService>();
builder.Services.AddScoped<ICalendarEventService, CalendarEventService>();
builder.Services.AddScoped<IDocumentService, DocumentService>();
builder.Services.AddScoped<ICandidateProfileService, CandidateProfileService>();
builder.Services.AddScoped<IExperienceService, ExperienceService>();
builder.Services.AddScoped<IEducationService, EducationService>();
builder.Services.AddScoped<IProjectService, ProjectService>();
builder.Services.AddScoped<ISkillService, SkillService>();
builder.Services.AddScoped<IScoringRuleService, ScoringRuleService>();
builder.Services.AddScoped<IAutomationRuleService, AutomationRuleService>();
builder.Services.AddScoped<IAutomationExecutionService, AutomationExecutionService>();
builder.Services.AddScoped<IAiModelConfigurationService, AiModelConfigurationService>();
builder.Services.AddScoped<IAiPromptTemplateService, AiPromptTemplateService>();
builder.Services.AddScoped<IActivityEntryService, ActivityEntryService>();
builder.Services.AddOpenApi();

if (builder.Environment.IsDevelopment())
{
    builder.Services.AddCors(options =>
        options.AddPolicy("BlazorDevelopment", policy =>
            policy.WithOrigins("http://localhost:5054", "https://localhost:7075")
                .WithMethods("GET", "POST", "PUT", "DELETE")
                .AllowAnyHeader()));
}

var app = builder.Build();

app.UseSerilogRequestLogging();

if (app.Environment.IsDevelopment())
{
	app.MapOpenApi();
}

app.UseHttpsRedirection();

if (app.Environment.IsDevelopment())
{
    app.UseCors("BlazorDevelopment");
}

app.UseAuthorization();

app.MapControllers();

app.Run();

public partial class Program;
