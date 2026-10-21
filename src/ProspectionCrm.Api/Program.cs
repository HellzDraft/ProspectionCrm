using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ProspectionCrm.Api.Security;
using ProspectionCrm.Api.Data;
using ProspectionCrm.Api.Services;
using ProspectionCrm.Api.Services.Collection;
using ProspectionCrm.Api.Storage;
using Serilog;
using ProspectionCrm.Api.Services.Automation;
using ProspectionCrm.Api.Services.Integrations;
using ProspectionCrm.Api.Services.Email;

var provisionOperator = args.Contains("--provision-operator", StringComparer.Ordinal);
var builder = WebApplication.CreateBuilder(provisionOperator ? [] : args);

builder.Services.AddSerilog((services, configuration) => configuration
    .ReadFrom.Configuration(builder.Configuration)
    .ReadFrom.Services(services)
    .Enrich.WithProperty("Environment", builder.Environment.EnvironmentName));

builder.Services.AddDbContext<ProspectionCrmDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException(
            "Configurez ConnectionStrings:DefaultConnection avec User Secrets ou une variable d'environnement.")));

// Includes the standard MVC antiforgery authorization filters (no view endpoints are mapped).
builder.Services.AddControllersWithViews();
builder.Services.AddCrmSecurity(builder.Configuration);
builder.Services.AddOptions<FileStorageOptions>()
    .Bind(builder.Configuration.GetRequiredSection(FileStorageOptions.SectionName))
    .Validate(options => !string.IsNullOrWhiteSpace(options.RootPath), "FileStorage:RootPath is required.")
    .ValidateOnStart();
builder.Services.AddSingleton<IFileStorage, LocalFileStorage>();
builder.Services.AddScoped<ICurrentWorkspaceProvider, CurrentWorkspaceProvider>();
builder.Services.AddSingleton<IAutomationSafetyPolicy, AutomationSafetyPolicy>();
builder.Services.AddSingleton<IAutomationRuleEvaluator, AutomationRuleEvaluator>();
builder.Services.AddScoped<IAutomationEventDispatcher, AutomationEventDispatcher>();
builder.Services.AddScoped<AutomationEvaluationService>();
builder.Services.AddScoped<AutomationSettingsService>();
builder.Services.AddOptions<AutomationJobQueueOptions>()
    .Validate(options => options.LeaseDuration > TimeSpan.Zero && options.LeaseDuration <= TimeSpan.FromHours(1),
        "Automation job lease duration must be positive and at most one hour.").ValidateOnStart();
builder.Services.AddScoped<IAutomationJobQueue, AutomationJobQueue>();
builder.Services.AddScoped<AutomationJobService>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<IntegrationStore>();
builder.Services.AddScoped<EmailAccountService>();
builder.Services.AddScoped<EmailSendPlanService>();
builder.Services.AddScoped<EmailSendDeliveryService>();
builder.Services.AddScoped<EmailSendDispatcher>();
builder.Services.AddScoped<IEmailSendTransport, UnavailableEmailSendTransport>();
builder.Services.AddScoped<InboundEmailImportService>();
builder.Services.AddScoped<EmailSyncService>();
builder.Services.AddScoped<EmailSyncDispatcher>();
builder.Services.AddScoped<HttpEmailSyncTransport>();
builder.Services.AddScoped<IEmailSyncTransport>(services => services.GetRequiredService<IOptionsMonitor<IntegrationOptions>>().CurrentValue.EmailSync.Enabled
    ? services.GetRequiredService<HttpEmailSyncTransport>() : new UnavailableEmailSyncTransport());
builder.Services.AddOptions<IntegrationOptions>().Bind(builder.Configuration.GetSection("Integrations"))
    .Validate<IOptions<OperatorOptions>>((options, human) => options.IsValid(human.Value), "Invalid integration security configuration.")
    .ValidateOnStart();
builder.Services.AddSingleton<IntegrationSendGate>();
builder.Services.AddScoped<IntegrationProbeService>();
builder.Services.AddHttpClient<IntegrationTransport>(client => client.Timeout = Timeout.InfiniteTimeSpan)
    .ConfigurePrimaryHttpMessageHandler(IntegrationTransport.CreateHandler);
builder.Services.AddOptions<AutomationWorkerOptions>()
    .Bind(builder.Configuration.GetRequiredSection(AutomationWorkerOptions.SectionName))
    .Validate(options => options.IsValid(), "AutomationWorker options are outside the supported bounds.")
    .ValidateOnStart();
builder.Services.AddScoped<AutomationRuntimeStore>();
builder.Services.AddScoped<AutomationCircuitBreakerService>();
builder.Services.AddScoped<AutomationRuntimeGuard>();
builder.Services.AddScoped<AutomationSupervisionWorkspace>();
builder.Services.AddScoped<AutomationSupervisionService>();
builder.Services.AddOptions<AutomationSupervisionOptions>()
    .Bind(builder.Configuration.GetSection(AutomationSupervisionOptions.SectionName))
    .Validate(options => options.IsValid(), "AutomationSupervision options are outside the supported bounds.")
    .ValidateOnStart();
builder.Services.AddScoped<AutomationActionRequestStore>();
builder.Services.AddScoped<AutomationActionRequestService>();
builder.Services.AddScoped<IAutomationWorkSource, AutomationWorkSource>();
builder.Services.AddScoped<IAutomationJobRecovery, AutomationJobRecovery>();
builder.Services.AddScoped<IAutomationJobProcessor, AutomationJobProcessor>();
builder.Services.AddScoped<IAutomationActionExecutor, CreateCrmTaskAutomationExecutor>();
builder.Services.AddSingleton<IAutomationWorkerDelay, AutomationWorkerDelay>();
builder.Services.AddHostedService<AutomationWorker>();
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
builder.Services.AddScoped<ISourceCollectionOrchestrator, SourceCollectionOrchestrator>();
builder.Services.AddOptions<SourceCollectionWorkerOptions>()
    .Bind(builder.Configuration.GetRequiredSection(SourceCollectionWorkerOptions.SectionName))
    .Validate(options => options.IsValid(), "SourceCollectionWorker options are outside the supported bounds.")
    .ValidateOnStart();
builder.Services.AddScoped<ISourceCollectionJobQueue, SourceCollectionJobQueue>();
builder.Services.AddScoped<SourceCollectionRetryPolicy>();
builder.Services.AddScoped<SourceCollectionJobFinalizer>();
builder.Services.AddScoped<RemoteCollectionState>();
builder.Services.AddScoped<RemoteCollectionDispatcher>();
builder.Services.AddScoped<RemoteCollectionResultProcessor>();
builder.Services.AddScoped<SourceCollectionJobGuard>();
builder.Services.AddScoped<ISourceCollectionJobRecovery, SourceCollectionJobRecovery>();
builder.Services.AddScoped<ISourceCollectionJobProcessor, SourceCollectionJobProcessor>();
builder.Services.AddHostedService<SourceCollectionWorker>();
builder.Services.AddOptions<SourceCollectionSchedulerOptions>()
    .Bind(builder.Configuration.GetRequiredSection(SourceCollectionSchedulerOptions.SectionName))
    .Validate(options => options.IsValid(), "SourceCollectionScheduler options are outside the supported bounds.")
    .ValidateOnStart();
builder.Services.AddScoped<SourceCollectionScheduleService>();
builder.Services.AddScoped<ISourceCollectionSchedulingService, SourceCollectionSchedulingService>();
builder.Services.AddHostedService<SourceCollectionScheduler>();
builder.Services.AddScoped<ISourceCollectionContextResolver, SourceCollectionContextResolver>();
builder.Services.AddScoped<ISourceCollectionJobService, SourceCollectionJobService>();
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

var app = builder.Build();

if (provisionOperator)
{
    await OperatorProvisioning.RunAsync(app.Services, args);
    return;
}

app.UseSerilogRequestLogging();
app.Use(async (context, next) =>
{
    if (!context.Request.IsHttps) { context.Response.StatusCode = 400; return; }
    try { await next(context); }
    catch (UnauthorizedAccessException) { context.Response.StatusCode = 403; }
    catch (IntegrationAccessException) { context.Response.StatusCode = 403; }
    catch (EmailSyncException error)
    { context.Response.StatusCode = error.Status; await context.Response.WriteAsJsonAsync(new { error = error.Message }); }
    catch (ProspectionCrm.Api.Dtos.Integrations.IntegrationContractException)
    { context.Response.StatusCode = 400; await context.Response.WriteAsJsonAsync(new { error = "invalid-integration-envelope" }); }
    catch (IntegrationConflictException error)
    { context.Response.StatusCode = 409; await context.Response.WriteAsJsonAsync(new { error = error.Message }); }
});

if (app.Environment.IsDevelopment())
{
	app.MapOpenApi();
}

app.UseRouting();
app.UseCors("Blazor");
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapControllers();

app.Run();

public partial class Program;
