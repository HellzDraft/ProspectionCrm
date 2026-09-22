using Microsoft.EntityFrameworkCore;
using ProspectionCrm.Api.Data;
using ProspectionCrm.Api.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<ProspectionCrmDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException(
            "Configurez ConnectionStrings:DefaultConnection avec User Secrets ou une variable d'environnement.")));

builder.Services.AddControllers();
builder.Services.AddScoped<IOpportunityService, OpportunityService>();
builder.Services.AddScoped<ICompanyService, CompanyService>();
builder.Services.AddScoped<IContactService, ContactService>();
builder.Services.AddScoped<ICrmTaskService, CrmTaskService>();
builder.Services.AddOpenApi();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
	app.MapOpenApi();
}

app.UseHttpsRedirection();

app.UseAuthorization();

app.MapControllers();

app.Run();
