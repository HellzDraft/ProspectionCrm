using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using ProspectionCrm.Blazor;
using ProspectionCrm.Blazor.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddScoped(sp => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });

builder.Services.AddSingleton<OpportunityService>();

builder.Services.AddSingleton<CompanyService>();

builder.Services.AddSingleton<ContactService>();

builder.Services.AddSingleton<FollowUpActionService>();

builder.Services.AddSingleton<DocumentReferenceService>();

await builder.Build().RunAsync();
