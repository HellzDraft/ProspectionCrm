using System.Net;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ProspectionCrm.Api.Dtos.Integrations;
using ProspectionCrm.Api.Security;
using ProspectionCrm.Api.Services.Calendar;
using ProspectionCrm.Api.Services.Integrations;
using Xunit;

namespace ProspectionCrm.Api.Tests;

public sealed class CalendarEventTransportTests(AutomationJobDatabase database) : CalendarEventDeliveryFixture(database), IClassFixture<AutomationJobDatabase>
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task HumanHttpDispatchSignsExactBytesRevalidatesAndAllowsCallbackReconcileBeforeAck(bool update)
    {
        Scenario? scenario = null; var provider = new Provider();
        CalendarEventCommand? sent = null;
        await using var server = await IntegrationTlsServer.StartAsync(async context =>
        {
            var s = scenario!;
            using var bytes = new MemoryStream(); await context.Request.Body.CopyToAsync(bytes);
            var raw = bytes.ToArray(); sent = CalendarEventContracts.ParseCommand(Encoding.UTF8.GetString(raw));
            Assert.Equal(IntegrationSignature.Sign(Convert.FromBase64String(s.Host.Integration.OutgoingKey.SecretBase64),
                IntegrationSignature.OutgoingAudience, s.Host.Integration.OutgoingKey.Id,
                context.Request.Headers[IntegrationSignature.TimeHeader].ToString(), "POST", context.Request.Path.ToString(), raw),
                context.Request.Headers[IntegrationSignature.SignatureHeader].ToString());
            // Both pre-network transactions released every SQL lock before the HTTP request.
            await using (var db = Db())
            {
                await using var tx = await db.Database.BeginTransactionAsync();
                await db.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout='2s'");
                await db.Database.ExecuteSqlInterpolatedAsync($"SELECT * FROM \"Calendars\" WHERE \"Id\"={s.Calendar.Id} FOR UPDATE");
                await db.Database.ExecuteSqlInterpolatedAsync($"SELECT * FROM \"CalendarEventDeliveries\" WHERE \"Id\"={sent.DeliveryId} FOR UPDATE");
            }
            await provider.Simulator.DispatchAsync(sent, default);
            await Prove(s, sent.DeliveryId, provider.Simulator.FindProof(sent.IdempotencyKey)!);
            context.Response.StatusCode = 202;
        });
        using var s = await Prepare(configure: host =>
        {
            host.Integration.CalendarEvent = new() { Enabled = true, Endpoint = server.Endpoint.AbsoluteUri };
            host.Integration.MaxConcurrentSends = 1; host.Integration.TimeoutSeconds = 10;
            host.TransportHandler = () => server.Handler();
        });
        scenario = s; var target = update ? await Target(s, provider, true) : null;
        var d = await Delivery(s, await Approved(s, target is null ? null : UpdateContent(s, target)));
        await Dispatch(s, d.Id); Assert.NotNull(sent); Assert.Equal("disabled", sent.Notifications);
        Assert.Equal("succeeded", (await ReadDelivery(s, d.Id)).StatusCode);
        Assert.Equal(1, server.Calls); Assert.Equal(1, provider.Simulator.EffectCount);
        Assert.Equal(HttpStatusCode.Conflict, (await s.Client.PostAsync(Route(d.Id, "dispatch"), null)).StatusCode);
        Assert.Equal(1, server.Calls);
    }

    [Theory]
    [InlineData(400)] [InlineData(401)] [InlineData(409)] [InlineData(429)] [InlineData(500)] [InlineData(503)]
    public async Task NoHttpErrorIsAProviderProofOrAnAutomaticRetry(int status)
    {
        await using var server = await IntegrationTlsServer.StartAsync(context => { context.Response.StatusCode = status; return Task.CompletedTask; });
        using var s = await Prepare(configure: host =>
        {
            host.Integration.CalendarEvent = new() { Enabled = true, Endpoint = server.Endpoint.AbsoluteUri };
            host.TransportHandler = () => server.Handler();
        });
        var d = await Delivery(s, await Approved(s)); await Dispatch(s, d.Id);
        Assert.Equal("operation-unknown", (await ReadDelivery(s, d.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await s.Client.PostAsync(Route(d.Id, "dispatch"), null)).StatusCode);
        await using var db = Db(); Assert.Single(await db.IntegrationAttempts.Where(x => x.OperationId == d.IntegrationOperationId).ToListAsync());
        Assert.Empty(await db.IntegrationReceipts.Where(x => x.OperationId == d.IntegrationOperationId).ToListAsync());
        Assert.Empty(await db.CalendarEvents.Where(x => x.WorkspaceId == s.Workspace).ToListAsync()); Assert.Equal(1, server.Calls);
    }

    [Theory]
    [InlineData("timeout")] [InlineData("oversized")] [InlineData("truncated")] [InlineData("encoding")]
    public async Task IncompleteOrInvalidAcknowledgementRemainsUnknown(string mode)
    {
        await using var server = await IntegrationTlsServer.StartAsync(async context =>
        {
            context.Response.StatusCode = 202;
            if (mode == "timeout") { await Task.Delay(1500); return; }
            if (mode == "encoding") context.Response.Headers.ContentEncoding = "gzip";
            if (mode == "truncated") { context.Response.ContentLength = 100; await context.Response.Body.WriteAsync("x"u8.ToArray()); context.Abort(); return; }
            if (mode == "oversized") await context.Response.Body.WriteAsync(new byte[4097]);
        });
        using var s = await Prepare(configure: host =>
        {
            host.Integration.CalendarEvent = new() { Enabled = true, Endpoint = server.Endpoint.AbsoluteUri };
            host.Integration.TimeoutSeconds = 1; host.TransportHandler = () => server.Handler();
        });
        var d = await Delivery(s, await Approved(s)); await Dispatch(s, d.Id);
        Assert.Equal("operation-unknown", (await ReadDelivery(s, d.Id)).StatusCode); Assert.Equal(1, server.Calls);
    }

    [Theory]
    [InlineData("approval")] [InlineData("account")] [InlineData("calendar")] [InlineData("connection")]
    [InlineData("hash")] [InlineData("message")] [InlineData("operation")] [InlineData("plan")]
    [InlineData("content")] [InlineData("settings")]
    public async Task PersistedAuthorizationAndFullCommandAreRecheckedAfterBeginBeforeAnyNetwork(string change)
    {
        using var s = await Prepare(); var d = await Delivery(s, await Approved(s));
        var (command, _) = await BeginWithoutTransport(s, d);
        await using var server = await IntegrationTlsServer.StartAsync(context => { context.Response.StatusCode = 202; return Task.CompletedTask; });
        s.Host.Integration.CalendarEvent = new() { Enabled = true, Endpoint = server.Endpoint.AbsoluteUri };
        await using var db = Db();
        if (change == "approval") await db.AutomationActionRequests.Where(x => x.Id == d.AutomationActionRequestId).ExecuteUpdateAsync(x => x.SetProperty(r => r.StatusCode, "rejected"));
        if (change == "account") await db.CalendarAccounts.Where(x => x.Id == s.Account.Id).ExecuteUpdateAsync(x => x.SetProperty(a => a.StatusCode, "disabled"));
        if (change == "calendar") await db.Calendars.Where(x => x.Id == s.Calendar.Id).ExecuteUpdateAsync(x => x.SetProperty(c => c.StatusCode, "disabled"));
        if (change == "settings") await db.AutomationRuntimeSettings.Where(x => x.WorkspaceId == s.Workspace).ExecuteUpdateAsync(x => x.SetProperty(r => r.IsEnabled, false));
        if (change == "connection") s.Host.Integration.ConnectionId = Guid.NewGuid();
        var forged = change switch
        {
            "hash" => command with { ContentHash = new string('0',64) },
            "message" => command with { MessageId = Guid.NewGuid() },
            "operation" => command with { OperationId = Guid.NewGuid() },
            "plan" => command with { Snapshot = command.Snapshot with { PlanId = Guid.NewGuid() } },
            "content" => command with { Snapshot = command.Snapshot with { Content = command.Snapshot.Content with { Title = "Unapproved" } } },
            _ => command
        };
        using var http = new HttpClient(server.Handler());
        using var scope = s.Host.Services.CreateScope();
        var transport = new HttpCalendarEventTransport(new(http, TimeProvider.System), new Monitor<IntegrationOptions>(s.Host.Integration),
            new Monitor<OperatorOptions>(s.Host.Operator), scope.ServiceProvider.GetRequiredService<CalendarEventDeliveryService>());
        Assert.Equal(CalendarEventTransportOutcome.Unknown, await transport.DispatchAsync(forged, default)); Assert.Equal(0, server.Calls);
    }

    [Fact]
    public async Task PreNetworkQuotaCheckDoesNotCountItsOwnAuthorizedAttemptTwice()
    {
        await using var server = await IntegrationTlsServer.StartAsync(context => { context.Response.StatusCode = 202; return Task.CompletedTask; });
        using var s = await Prepare(configure: host =>
        {
            host.Integration.CalendarEvent = new() { Enabled = true, Endpoint = server.Endpoint.AbsoluteUri };
            host.TransportHandler = () => server.Handler();
        });
        await using var db = Db(); await db.AutomationRuntimeSettings.Where(x => x.WorkspaceId == s.Workspace).ExecuteUpdateAsync(x => x.SetProperty(r => r.MaxExecutionsPerMinute,1));
        var d = await Delivery(s, await Approved(s)); await Dispatch(s,d.Id); Assert.Equal(1,server.Calls);
        Assert.Equal("awaiting-result",(await ReadDelivery(s,d.Id)).StatusCode);
    }

    [Theory]
    [InlineData("")] [InlineData("http://localhost/event")] [InlineData("https://user@localhost/event")]
    [InlineData("https://localhost/event?callback=other")] [InlineData("https://localhost/event#fragment")]
    public async Task UnsafeAdministratorEndpointIsRejected(string endpoint)
    {
        using var s = await Prepare(); s.Host.Integration.CalendarEvent = new() { Enabled = true, Endpoint = endpoint };
        Assert.False(s.Host.Integration.IsValid(s.Host.Operator));
    }

    [Fact]
    public async Task SeparateDisabledDefaultRequiresBothSwitches()
    {
        using var s = await Prepare(); Assert.False(s.Host.Integration.CalendarEvent.Enabled); Assert.Equal("", s.Host.Integration.CalendarEvent.Endpoint);
        s.Host.Integration.CalendarEvent = new() { Enabled = true, Endpoint = "https://localhost/event" };
        Assert.True(s.Host.Integration.IsValid(s.Host.Operator)); s.Host.Integration.Enabled = false;
        Assert.False(s.Host.Integration.IsValid(s.Host.Operator)); s.Host.Integration.CalendarEvent.Enabled = false;
        Assert.True(s.Host.Integration.IsValid(s.Host.Operator));
    }
    private sealed class Monitor<T>(T value) : IOptionsMonitor<T>
    { public T CurrentValue => value; public T Get(string? name) => value; public IDisposable? OnChange(Action<T,string?> listener) => null; }
}
