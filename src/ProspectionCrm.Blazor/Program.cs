using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using ProspectionCrm.Blazor;
using ProspectionCrm.Blazor.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

var apiBaseUrl = builder.Configuration["ApiBaseUrl"]
    ?? throw new InvalidOperationException("La configuration ApiBaseUrl est requise.");
var api = new Uri(apiBaseUrl);
if (api.Scheme != "https" || !string.IsNullOrEmpty(api.UserInfo))
    throw new InvalidOperationException("ApiBaseUrl doit être une URL HTTPS.");
builder.Services.AddScoped<SessionState>();
builder.Services.AddScoped<SessionService>();
builder.Services.AddScoped(sp => new HttpClient(new ApiCookieHandler(api, sp.GetRequiredService<SessionState>(),
    sp.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>()) { InnerHandler = new HttpClientHandler() }) { BaseAddress = api });
builder.Services.AddScoped<OpportunityApiService>();
builder.Services.AddScoped<PipelineApiService>();


builder.Services.AddScoped<CompanyApiService>();

builder.Services.AddScoped<ContactApiService>();

builder.Services.AddScoped<CrmTaskApiService>();

builder.Services.AddSingleton<DocumentReferenceService>();

await builder.Build().RunAsync();
