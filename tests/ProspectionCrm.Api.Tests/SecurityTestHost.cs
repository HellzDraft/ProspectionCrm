using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ProspectionCrm.Api.Entities;
using ProspectionCrm.Api.Security;
using ProspectionCrm.Api.Services.Integrations;
using Xunit;

namespace ProspectionCrm.Api.Tests;

// All credentials here are synthetic test fixtures, in memory only; no system trust changes.
public sealed class SecurityTestHost(string connectionString, Guid user, Guid workspace) : WebApplicationFactory<Program>
{
    public const string Password = "Synthetic-test-password-932!";
    public OperatorOptions Operator { get; } = new() { UserId = user, WorkspaceId = workspace };
    public IntegrationOptions Integration { get; } = new()
    {
        Enabled = true, WorkspaceId = workspace, ConnectionId = Guid.NewGuid(), Endpoint = "https://localhost:1/probe", TimeoutSeconds = 1
    };
    public TestClock Clock { get; } = new();
    public Func<HttpMessageHandler>? TransportHandler { get; set; }
    public ProspectionCrm.Api.Services.Email.IEmailSendTransport? EmailSendTransport { get; set; }
    public ProspectionCrm.Api.Services.Calendar.ICalendarEventTransport? CalendarEventTransport { get; set; }
    public bool ConfigureOperator { get; set; } = true;

    public static async Task<SecurityTestHost> CreateAsync(AutomationJobDatabase database, Guid? existingWorkspace = null)
    {
        await using var db = database.Db();
        Workspace workspace;
        if (existingWorkspace is { } id) workspace = await db.Workspaces.FindAsync(id) ?? throw new InvalidOperationException();
        else
        {
            workspace = new() { Name = "Security", TimeZoneId = "UTC", OwnerUser = new() { Email = $"{Guid.NewGuid():N}@example.invalid" } };
            db.Add(workspace); await db.SaveChangesAsync();
        }
        var host = new SecurityTestHost(database.Postgres.GetConnectionString(), workspace.OwnerUserId, workspace.Id);
        host.Operator.PasswordHash = new PasswordHasher<OperatorOptions>().HashPassword(host.Operator, Password);
        host.Integration.OutgoingKey = Key("out-1", 17, workspace.Id, host.Integration.ConnectionId);
        host.Integration.IncomingKeys = [Key("in-1", 42, workspace.Id, host.Integration.ConnectionId),
            Key("wrong-workspace", 43, Guid.NewGuid(), host.Integration.ConnectionId),
            Key("wrong-connection", 44, workspace.Id, Guid.NewGuid()),
            Key("revoked", 45, workspace.Id, host.Integration.ConnectionId, false)];
        return host;
    }
    public static IntegrationKey Key(string id, byte seed, Guid workspace, Guid connection, bool enabled = true) => new()
    { Id = id, Enabled = enabled, SecretBase64 = Convert.ToBase64String(Enumerable.Repeat(seed, 32).ToArray()), WorkspaceId = workspace, ConnectionId = connection };

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing").UseSetting("ConnectionStrings:DefaultConnection", connectionString);
        builder.ConfigureAppConfiguration((_, config) => config.AddJsonStream(new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(new
        {
            Operator = ConfigureOperator ? Operator : new OperatorOptions(), Integrations = Integration,
            AutomationWorker = new { Enabled = false }, SourceCollectionWorker = new { Enabled = false }, SourceCollectionScheduler = new { Enabled = false }
        }))));
        builder.ConfigureTestServices(services =>
        {
            services.AddDataProtection().UseEphemeralDataProtectionProvider();
            services.AddSingleton<IStartupFilter, TestServerRawTarget>();
            services.RemoveAll<TimeProvider>(); services.AddSingleton<TimeProvider>(Clock);
            if (TransportHandler is not null) services.AddHttpClient<IntegrationTransport>().ConfigurePrimaryHttpMessageHandler(TransportHandler);
            if (EmailSendTransport is not null)
            {
                services.RemoveAll<ProspectionCrm.Api.Services.Email.IEmailSendTransport>();
                services.AddSingleton(EmailSendTransport);
            }
            if (CalendarEventTransport is not null)
            {
                services.RemoveAll<ProspectionCrm.Api.Services.Calendar.ICalendarEventTransport>();
                services.AddSingleton(CalendarEventTransport);
            }
        });
    }
    // TestServer omits RawTarget; supply the HTTP feature that Kestrel provides in production.
    private sealed class TestServerRawTarget : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, following) =>
            {
                var feature = context.Features.Get<IHttpRequestFeature>()!;
                if (string.IsNullOrEmpty(feature.RawTarget)) feature.RawTarget = context.Request.PathBase.ToUriComponent()
                    + context.Request.Path.ToUriComponent() + context.Request.QueryString;
                return following(context);
            });
            next(app);
        };
    }
    public HttpClient Client() => CreateClient(new() { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
    public static async Task CsrfAsync(HttpClient client)
    {
        var body = await client.GetFromJsonAsync<JsonElement>("/api/session/antiforgery");
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", body.GetProperty("token").GetString());
    }
    public static async Task LoginAsync(HttpClient client)
    {
        await CsrfAsync(client);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync("/api/session/login", new { password = Password })).StatusCode);
        await CsrfAsync(client);
    }
    public HttpRequestMessage Signed(byte[] body, string keyId = "in-1", long? time = null, string target = IntegrationSignature.ResultTarget)
    {
        var key = Integration.IncomingKeys.Single(k => k.Id == keyId);
        var timestamp = (time ?? Clock.GetUtcNow().ToUnixTimeSeconds()).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var request = new HttpRequestMessage(HttpMethod.Post, target) { Content = new ByteArrayContent(body) };
        request.Content.Headers.ContentType = new("application/json") { CharSet = "utf-8" };
        request.Headers.Add(IntegrationSignature.KeyHeader, key.Id);
        request.Headers.Add(IntegrationSignature.TimeHeader, timestamp);
        request.Headers.Add(IntegrationSignature.SignatureHeader, IntegrationSignature.Sign(Convert.FromBase64String(key.SecretBase64),
            IntegrationSignature.IncomingAudience, key.Id, timestamp, "POST", target, body));
        return request;
    }
    public sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
