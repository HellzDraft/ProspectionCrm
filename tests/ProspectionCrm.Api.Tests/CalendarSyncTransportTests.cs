using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ProspectionCrm.Api.Dtos.CalendarEvents;
using ProspectionCrm.Api.Dtos.Integrations;
using ProspectionCrm.Api.Services.Calendar;
using ProspectionCrm.Api.Services.Integrations;
using Xunit;

namespace ProspectionCrm.Api.Tests;

public sealed class CalendarSyncTransportTests(AutomationJobDatabase database) : CalendarSyncFixture(database), IClassFixture<AutomationJobDatabase>
{
    [Fact]
    public async Task HumanDispatchUsesSharedTlsHmacAndAcceptsCallbackBeforeAck()
    {
        using var host = await SecurityTestHost.CreateAsync(Database);
        string? actualSignature = null, expectedSignature = null;
        CalendarSyncCommand? received = null;
        await using var server = await IntegrationTlsServer.StartAsync(async context =>
        {
            using var raw = new MemoryStream(); await context.Request.Body.CopyToAsync(raw);
            var bytes = raw.ToArray(); received = CalendarSyncContracts.ParseCommand(Encoding.UTF8.GetString(bytes));
            actualSignature = context.Request.Headers[IntegrationSignature.SignatureHeader];
            expectedSignature = IntegrationSignature.Sign(Convert.FromBase64String(host.Integration.OutgoingKey.SecretBase64),
                IntegrationSignature.OutgoingAudience, host.Integration.OutgoingKey.Id,
                context.Request.Headers[IntegrationSignature.TimeHeader].ToString(), "POST", context.Request.Path.ToString(), bytes);
            var result = new CalendarSyncResult(1, Guid.NewGuid(), received.OperationId, received.CorrelationId, received.MessageId,
                received.Type, new(received.Payload.CalendarRef, received.Payload.JobRef, "succeeded", "http-cursor", []));
            using var machine = host.Client();
            var response = await machine.SendAsync(host.Signed(Encoding.UTF8.GetBytes(CalendarSyncContracts.Serialize(result))));
            context.Response.StatusCode = response.StatusCode == HttpStatusCode.Accepted ? 202 : 500;
        });
        host.Integration.CalendarSync = new() { Enabled = true, Endpoint = new UriBuilder(server.Endpoint) { Path = "/webhook/calendar-sync" }.Uri.AbsoluteUri };
        host.Integration.TimeoutSeconds = 10;
        host.TransportHandler = () => server.Handler();
        using var client = host.Client(); await SecurityTestHost.LoginAsync(client);
        var accountResponse = await client.PostAsJsonAsync("/api/calendar-accounts", new CreateCalendarAccountRequest(host.Integration.ConnectionId,
            "synthetic", "http-account", "Synthetic account"));
        Assert.Equal(HttpStatusCode.Created, accountResponse.StatusCode);
        var account = (await accountResponse.Content.ReadFromJsonAsync<CalendarAccountDto>())!;
        var calendar = (await (await client.PostAsJsonAsync($"/api/calendar-accounts/{account.Id}/calendars", new CreateCalendarRequest("http-calendar", "Synthetic", "Europe/Paris")))
            .Content.ReadFromJsonAsync<CalendarDto>())!;
        var job = (await (await client.PostAsJsonAsync($"/api/calendars/{calendar.Id}/sync-jobs", new EnqueueCalendarSyncRequest(10)))
            .Content.ReadFromJsonAsync<CalendarSyncJobDto>())!;
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync($"/api/calendar-sync-jobs/{job.Id}/dispatch", null)).StatusCode);
        Assert.Equal(1, server.Calls); Assert.NotNull(received); Assert.Equal(expectedSignature, actualSignature);
        Assert.Equal(calendar.Id, received.Payload.CalendarRef); Assert.Null(received.Payload.PreviousCursor); Assert.Equal(10, received.Payload.MaxItems);
        await using var db = Db();
        Assert.Equal("result-received", (await db.CalendarSyncJobs.AsNoTracking().SingleAsync(x => x.Id == job.Id)).StatusCode);
        Assert.Equal("acknowledged", (await db.IntegrationAttempts.SingleAsync(x => x.OperationId == job.IntegrationOperationId)).StatusCode);
        Assert.Null((await db.CalendarSyncStates.AsNoTracking().SingleAsync(x => x.CalendarId == calendar.Id)).Cursor);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync($"/api/calendar-sync-jobs/{job.Id}/process-result", null)).StatusCode);
        Assert.Equal("http-cursor", (await db.CalendarSyncStates.AsNoTracking().SingleAsync(x => x.CalendarId == calendar.Id)).Cursor);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync($"/api/calendar-sync-jobs/{job.Id}/dispatch", null)).StatusCode);
        Assert.Equal(1, server.Calls);
    }

    [Fact]
    public async Task DispatchRequiresHumanAndCsrfAndDisabledDefaultCreatesNoAttempt()
    {
        using var s = await Prepare(dispatch: false, receive: false);
        var path = $"/api/calendar-sync-jobs/{s.Job.Id}/dispatch";
        using var anonymous = s.Host.Client();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync(path, null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.SendAsync(s.Host.Signed("{}"u8.ToArray(), target: path))).StatusCode);
        s.Client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Client.PostAsync(path, null)).StatusCode);
        await SecurityTestHost.CsrfAsync(s.Client);
        var response = await s.Client.PostAsync(path, null);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode); Assert.Contains("CalendarSyncTransportDisabled", await response.Content.ReadAsStringAsync());
        Assert.Equal("queued", (await s.Client.GetFromJsonAsync<CalendarSyncJobDto>(path.Replace("/dispatch", "")))!.StatusCode);
        await using var db = Db(); Assert.Empty(await db.IntegrationAttempts.Where(x => x.OperationId == s.Job.IntegrationOperationId).ToListAsync());
    }

    [Theory]
    [InlineData("")][InlineData("http://localhost/email")][InlineData("https://user@localhost/email")]
    [InlineData("https://localhost/email?target=x")][InlineData("https://localhost/email#fragment")]
    public async Task EnabledEmailTransportRejectsUnsafeConfiguration(string endpoint)
    {
        using var host = await SecurityTestHost.CreateAsync(Database);
        host.Integration.CalendarSync = new() { Enabled = true, Endpoint = endpoint };
        Assert.False(host.Integration.IsValid(host.Operator));
    }

    [Fact]
    public async Task EmailTransportNeedsBothSwitchesAndRetainsTheGenericEndpoint()
    {
        using var host = await SecurityTestHost.CreateAsync(Database);
        var genericEndpoint = host.Integration.Endpoint;
        host.Integration.CalendarSync = new() { Enabled = true, Endpoint = "https://localhost/email" };
        Assert.True(host.Integration.IsValid(host.Operator));
        host.Integration.Enabled = false; Assert.False(host.Integration.IsValid(host.Operator));
        host.Integration.CalendarSync.Enabled = false; Assert.True(host.Integration.IsValid(host.Operator));
        Assert.Equal(genericEndpoint, host.Integration.Endpoint);
    }

    [Fact]
    public async Task MismatchedAccountFailsClosedBeforeHttp()
    {
        using var s = await Prepare(dispatch: false, receive: false);
        var calls = 0;
        await using var server = await IntegrationTlsServer.StartAsync(c => { calls++; c.Response.StatusCode = 202; return Task.CompletedTask; });
        s.Host.Integration.CalendarSync = new() { Enabled = true, Endpoint = server.Endpoint.AbsoluteUri };
        await using var db = Db();
        using var client = new HttpClient(server.Handler()); using var gate = new IntegrationSendGate(Options.Create(s.Host.Integration));
        var transport = new HttpCalendarSyncTransport(new(client, TimeProvider.System), gate,
            new Monitor<IntegrationOptions>(s.Host.Integration), new Monitor<ProspectionCrm.Api.Security.OperatorOptions>(s.Host.Operator), db);
        var op = await db.IntegrationOperations.SingleAsync(x => x.Id == s.Job.IntegrationOperationId);
        var forged = new CalendarSyncCommand(1, op.CommandMessageId, op.Id, op.CorrelationId, "calendar.sync", new(Guid.NewGuid(), s.Job.Id, null, 100));
        Assert.Equal(IntegrationTransportOutcome.Failed, await transport.DispatchAsync(forged, default)); Assert.Equal(0, calls);
    }
    [Theory]
    [InlineData("calendar")][InlineData("job")][InlineData("operation")][InlineData("connection")]
    [InlineData("message")][InlineData("cursor")][InlineData("gate")]
    public async Task DurableCommandBindingAndSingleConcurrencyGateFailClosed(string mismatch)
    {
        using var s = await Prepare(dispatch: false, receive: false);
        await using var db = Db();
        var (command, _) = await Sync(s, db).BeginAsync(s.Workspace, s.Job.Id, default);
        await using var server = await IntegrationTlsServer.StartAsync(c => { c.Response.StatusCode = 202; return Task.CompletedTask; });
        s.Host.Integration.CalendarSync = new() { Enabled = true, Endpoint = server.Endpoint.AbsoluteUri };
        s.Host.Integration.MaxConcurrentSends = 1;
        using var client = new HttpClient(server.Handler()); using var gate = new IntegrationSendGate(Options.Create(s.Host.Integration));
        var transport = new HttpCalendarSyncTransport(new(client, TimeProvider.System), gate,
            new Monitor<IntegrationOptions>(s.Host.Integration), new Monitor<ProspectionCrm.Api.Security.OperatorOptions>(s.Host.Operator), db);
        var forged = mismatch switch {
            "calendar" => command with { Payload = command.Payload with { CalendarRef = Guid.NewGuid() } },
            "job" => command with { Payload = command.Payload with { JobRef = Guid.NewGuid() } },
            "operation" => command with { OperationId = Guid.NewGuid() },
            "message" => command with { MessageId = Guid.NewGuid() },
            "cursor" => command with { Payload = command.Payload with { PreviousCursor = "wrong" } },
            _ => command
        };
        var connection = s.Host.Integration.ConnectionId;
        if (mismatch == "connection") s.Host.Integration.ConnectionId = Guid.NewGuid();
        if (mismatch == "gate") Assert.True(await gate.EnterAsync(default));
        Assert.Equal(IntegrationTransportOutcome.Failed, await transport.DispatchAsync(forged, default));
        Assert.Equal(0, server.Calls);
        s.Host.Integration.ConnectionId = connection;
        if (mismatch == "gate") gate.Exit();
        Assert.Equal(IntegrationTransportOutcome.Acknowledged, await transport.DispatchAsync(command, default));
        Assert.Equal(1, server.Calls); // MaxConcurrentSends=1 also proves no double gate acquisition.
    }

}
