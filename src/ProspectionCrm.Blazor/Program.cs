using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using ProspectionCrm.Blazor;
using ProspectionCrm.Blazor.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

var apiBaseUrl = builder.Configuration["ApiBaseUrl"]
    ?? throw new InvalidOperationException("La configuration ApiBaseUrl est requise.");
builder.Services.AddScoped(sp => new HttpClient { BaseAddress = new Uri(apiBaseUrl) });
builder.Services.AddScoped<OpportunityApiService>();


builder.Services.AddScoped<CompanyApiService>();

builder.Services.AddScoped<ContactApiService>();

builder.Services.AddSingleton<FollowUpActionService>();

builder.Services.AddSingleton<DocumentReferenceService>();

await builder.Build().RunAsync();
