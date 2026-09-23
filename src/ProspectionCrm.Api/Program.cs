using Microsoft.EntityFrameworkCore;
using ProspectionCrm.Api.Data;
using ProspectionCrm.Api.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<ProspectionCrmDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException(
            "Configurez ConnectionStrings:DefaultConnection avec User Secrets ou une variable d'environnement.")));

builder.Services.AddControllers();
builder.Services.AddScoped<ICurrentWorkspaceProvider, CurrentWorkspaceProvider>();
builder.Services.AddScoped<IPipelineService, PipelineService>();
builder.Services.AddScoped<IOpportunityService, OpportunityService>();
builder.Services.AddScoped<ICompanyService, CompanyService>();
builder.Services.AddScoped<IContactService, ContactService>();
builder.Services.AddScoped<ICrmTaskService, CrmTaskService>();
builder.Services.AddScoped<ISourceConfigurationService, SourceConfigurationService>();
builder.Services.AddScoped<ISavedSearchService, SavedSearchService>();
builder.Services.AddScoped<ISourceExecutionService, SourceExecutionService>();
builder.Services.AddScoped<IOpportunitySourceService, OpportunitySourceService>();
builder.Services.AddScoped<ICampaignService, CampaignService>();
builder.Services.AddScoped<IApplicationService, ApplicationService>();
builder.Services.AddScoped<IProposalService, ProposalService>();
builder.Services.AddScoped<IEmailMessageService, EmailMessageService>();
builder.Services.AddScoped<ICalendarEventService, CalendarEventService>();
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
