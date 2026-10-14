using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProspectionCrm.Api.Data;
using ProspectionCrm.Api.Security;
using ProspectionCrm.Api.Services;

namespace ProspectionCrm.Api.Tests;

// Explicit test-only identity for historical business scenarios, including deliberately corrupt setup states.
// SecurityTests use WebApplicationFactory<Program> directly, real cookies/HMAC and real antiforgery.
public sealed class BusinessTestFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.ConfigureServices(services =>
    {
        services.AddDataProtection().UseEphemeralDataProtectionProvider();
        services.AddTransient<BusinessAuthenticationHandler>();
        services.PostConfigure<AuthenticationOptions>(o => o.SchemeMap[SecurityNames.Human].HandlerType = typeof(BusinessAuthenticationHandler));
        services.Configure<MvcOptions>(o => o.Filters.Add(new IgnoreAntiforgeryTokenAttribute { Order = 1001 }));
        services.RemoveAll<ICurrentWorkspaceProvider>();
        services.AddScoped<ICurrentWorkspaceProvider, TestWorkspaceProvider>();
        services.RemoveAll<IHumanActor>();
        services.AddScoped<IHumanActor>(sp => new TestHumanActor(sp.GetRequiredService<ProspectionCrmDbContext>(),
            sp.GetRequiredService<ICurrentWorkspaceProvider>()));
    });
}

public sealed class BusinessAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger, UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(AuthenticateResult.Success(
        new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity([
            new(ClaimTypes.NameIdentifier, "business-fixture-operator")], SecurityNames.Human)), SecurityNames.Human)));
}

// Selection is local to business tests; it is no longer an application authentication mechanism.
public sealed class TestWorkspaceProvider(ProspectionCrmDbContext db) : ICurrentWorkspaceProvider
{
    public async Task<Guid> GetCurrentWorkspaceIdAsync(CancellationToken cancellationToken = default)
    {
        var ids = await db.Workspaces.Where(x => x.ArchivedAt == null).Select(x => x.Id).Take(2).ToListAsync(cancellationToken);
        return ids.Count == 1 ? ids[0] : throw new InvalidOperationException("Test fixture needs one active workspace.");
    }
}

public sealed class TestHumanActor(ProspectionCrmDbContext db, ICurrentWorkspaceProvider provider) : IHumanActor
{
    public TestHumanActor(ProspectionCrmDbContext db, Guid workspace) : this(db, new FixedWorkspace(workspace)) { }
    public async Task<(Guid UserId, Guid WorkspaceId)> RequireAsync(CancellationToken token = default)
    {
        var workspace = await provider.GetCurrentWorkspaceIdAsync(token);
        var owner = await db.Workspaces.Where(x => x.Id == workspace && x.ArchivedAt == null).Select(x => x.OwnerUserId).SingleAsync(token);
        return (owner, workspace);
    }
    private sealed class FixedWorkspace(Guid id) : ICurrentWorkspaceProvider
    { public Task<Guid> GetCurrentWorkspaceIdAsync(CancellationToken cancellationToken = default) => Task.FromResult(id); }
}
