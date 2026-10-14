using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ProspectionCrm.Api.Security;
using ProspectionCrm.Api.Services.Integrations;
using Xunit;

namespace ProspectionCrm.Api.Tests;

public sealed class OperatorSecurityTests(AutomationJobDatabase database) : IClassFixture<AutomationJobDatabase>
{
    [Fact]
    public async Task LoginSessionAntiforgeryAndLogoutUseRealCookies()
    {
        using var host = await SecurityTestHost.CreateAsync(database); using var client = host.Client();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/session")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/session/login", new { password = SecurityTestHost.Password })).StatusCode);
        await SecurityTestHost.CsrfAsync(client);
        var invalid = await client.PostAsJsonAsync("/api/session/login", new { password = "invalid-test-password" });
        Assert.Equal(HttpStatusCode.Unauthorized, invalid.StatusCode);
        Assert.DoesNotContain("password", await invalid.Content.ReadAsStringAsync());
        var login = await client.PostAsJsonAsync("/api/session/login", new { password = SecurityTestHost.Password, userId = Guid.NewGuid(), role = "admin" });
        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
        var cookie = Assert.Single(login.Headers.GetValues("Set-Cookie"), c => c.StartsWith("__Host-ProspectionCrm="));
        Assert.Contains("secure", cookie); Assert.Contains("httponly", cookie); Assert.Contains("samesite=strict", cookie);
        var session = await client.GetFromJsonAsync<JsonElement>("/api/session");
        Assert.Equal(host.Operator.UserId, session.GetProperty("userId").GetGuid());
        Assert.Equal(host.Operator.WorkspaceId, session.GetProperty("workspaceId").GetGuid());
        // The anonymous CSRF token cannot be reused under the authenticated identity.
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/api/session/logout", null)).StatusCode);
        await SecurityTestHost.CsrfAsync(client);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("/api/session/logout", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/session")).StatusCode);
    }

    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task MissingOrInvalidConfigurationFailsClosed(bool malformed)
    {
        using var host = await SecurityTestHost.CreateAsync(database);
        host.Integration.Enabled = false;
        if (malformed) host.Operator.PasswordHash = "invalid"; else host.ConfigureOperator = false;
        using var client = host.Client(); await SecurityTestHost.CsrfAsync(client);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/session/login", new { password = SecurityTestHost.Password })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/companies")).StatusCode);
    }

    [Fact]
    public async Task LoginAttemptsAreGloballyBounded()
    {
        using var host = await SecurityTestHost.CreateAsync(database); using var client = host.Client();
        await SecurityTestHost.CsrfAsync(client);
        for (var i = 0; i < 5; i++) Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.PostAsJsonAsync("/api/session/login", new { password = "incorrect-password" })).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.PostAsJsonAsync("/api/session/login", new { password = SecurityTestHost.Password })).StatusCode);
    }

    [Fact]
    public async Task CredentialChangeInvalidatesExistingSession()
    {
        using var host = await SecurityTestHost.CreateAsync(database); using var client = host.Client();
        await SecurityTestHost.LoginAsync(client);
        host.Services.GetRequiredService<IConfiguration>()["Operator:PasswordHash"] = "";
        host.Services.GetRequiredService<IOptionsMonitorCache<OperatorOptions>>().TryRemove(Options.DefaultName);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/session")).StatusCode);
    }

    [Theory]
    [InlineData("expiry")][InlineData("owner")][InlineData("archived")]
    public async Task SessionExpiresWithoutRenewalAndRechecksOwnership(string change)
    {
        using var host = await SecurityTestHost.CreateAsync(database); using var client = host.Client();
        await SecurityTestHost.LoginAsync(client);
        if (change == "expiry") host.Clock.Now += TimeSpan.FromHours(8) + TimeSpan.FromSeconds(1);
        else
        {
            await using var db = database.Db();
            if (change == "archived") await db.Workspaces.Where(w => w.Id == host.Operator.WorkspaceId).ExecuteUpdateAsync(s => s.SetProperty(w => w.ArchivedAt, DateTimeOffset.UtcNow));
            else
            {
                var user = new ProspectionCrm.Api.Entities.UserAccount { Email = $"{Guid.NewGuid():N}@example.invalid" };
                db.Add(user); await db.SaveChangesAsync();
                await db.Workspaces.Where(w => w.Id == host.Operator.WorkspaceId).ExecuteUpdateAsync(s => s.SetProperty(w => w.OwnerUserId, user.Id));
            }
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/session")).StatusCode);
    }

    [Theory]
    [InlineData("GET", "/api/companies")]
    [InlineData("GET", "/api/documents")]
    [InlineData("GET", "/api/automation-settings")]
    [InlineData("GET", "/api/automation-supervision")]
    [InlineData("POST", "/api/setup/bootstrap")]
    [InlineData("POST", "/api/setup/initial-pipelines")]
    [InlineData("POST", "/api/source-collection-jobs")]
    [InlineData("POST", "/api/automation-jobs")]
    [InlineData("POST", "/api/automation-circuit-breaker/reset")]
    [InlineData("POST", "/api/automation-action-requests/00000000-0000-0000-0000-000000000001/approve")]
    [InlineData("POST", "/api/integrations/probes")]
    public async Task AnonymousAndMachineCannotAccessHumanRoutesWithOrWithoutHeaders(string method, string route)
    {
        using var host = await SecurityTestHost.CreateAsync(database); using var client = host.Client();
        using var anonymous = new HttpRequestMessage(new HttpMethod(method), route);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(anonymous)).StatusCode);
        using var machine = host.Signed("{}"u8.ToArray(), target: route); machine.Method = new HttpMethod(method);
        var response = await client.SendAsync(machine);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode); Assert.Null(response.Headers.Location);
    }

    [Fact]
    public async Task CorsOnlyAllowsExplicitHttpsOriginAndCredentialsAndPatch()
    {
        using var host = await SecurityTestHost.CreateAsync(database); using var client = host.Client();
        foreach (var origin in new[] { "https://localhost:7075", "https://evil.example", "http://localhost:5054", "https://127.0.0.1:7075" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Options, "/api/companies");
            request.Headers.Add("Origin", origin); request.Headers.Add("Access-Control-Request-Method", "PATCH");
            request.Headers.Add("Access-Control-Request-Headers", "content-type,x-csrf-token");
            var response = await client.SendAsync(request);
            if (origin == "https://localhost:7075")
            {
                Assert.Equal(origin, Assert.Single(response.Headers.GetValues("Access-Control-Allow-Origin")));
                Assert.Equal("true", Assert.Single(response.Headers.GetValues("Access-Control-Allow-Credentials")));
                Assert.Contains("PATCH", string.Join(",", response.Headers.GetValues("Access-Control-Allow-Methods")));
            }
            else Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
        }
        await SecurityTestHost.LoginAsync(client); client.DefaultRequestHeaders.Add("Origin", "https://localhost:7075");
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/companies")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsync("/api/integrations/probes", null)).StatusCode);
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/api/integrations/probes", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync(IntegrationSignature.ResultTarget, new StringContent("{}"))).StatusCode);
    }
}
