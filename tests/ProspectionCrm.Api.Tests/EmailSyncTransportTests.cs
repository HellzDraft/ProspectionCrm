using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ProspectionCrm.Api.Dtos.EmailMessages;
using ProspectionCrm.Api.Dtos.Integrations;
using ProspectionCrm.Api.Services.Email;
using ProspectionCrm.Api.Services.Integrations;
using Xunit;

namespace ProspectionCrm.Api.Tests;

public sealed class EmailSyncTransportTests(AutomationJobDatabase database) : EmailSyncFixture(database), IClassFixture<AutomationJobDatabase>
{
    [Fact]
    public async Task HumanDispatchUsesSharedTlsHmacAndAcceptsCallbackBeforeAck()
    {
        using var host = await SecurityTestHost.CreateAsync(Database);
        string? actualSignature = null, expectedSignature = null;
        EmailSyncCommand? received = null;
        await using var server = await IntegrationTlsServer.StartAsync(async context =>
        {
            using var raw = new MemoryStream(); await context.Request.Body.CopyToAsync(raw);
            var bytes = raw.ToArray(); received = EmailSyncContracts.ParseCommand(Encoding.UTF8.GetString(bytes));
            actualSignature = context.Request.Headers[IntegrationSignature.SignatureHeader];
            expectedSignature = IntegrationSignature.Sign(Convert.FromBase64String(host.Integration.OutgoingKey.SecretBase64),
                IntegrationSignature.OutgoingAudience, host.Integration.OutgoingKey.Id,
                context.Request.Headers[IntegrationSignature.TimeHeader].ToString(), "POST", context.Request.Path.ToString(), bytes);
            var result = new EmailSyncResult(1, Guid.NewGuid(), received.OperationId, received.CorrelationId, received.MessageId,
                received.Type, new(received.Payload.AccountRef, "succeeded", "http-cursor", []));
            using var machine = host.Client();
            var response = await machine.SendAsync(host.Signed(Encoding.UTF8.GetBytes(EmailSyncContracts.Serialize(result))));
            context.Response.StatusCode = response.StatusCode == HttpStatusCode.Accepted ? 202 : 500;
        });
        host.Integration.EmailSync = new() { Enabled = true, Endpoint = new UriBuilder(server.Endpoint) { Path = "/webhook/email-sync" }.Uri.AbsoluteUri };
        host.Integration.TimeoutSeconds = 10;
        host.TransportHandler = () => server.Handler();
        using var client = host.Client(); await SecurityTestHost.LoginAsync(client);
        var accountResponse = await client.PostAsJsonAsync("/api/email-accounts", new CreateEmailAccountRequest(host.Integration.ConnectionId,
            "synthetic", "http-account", "inbox@example.invalid"));
        Assert.Equal(HttpStatusCode.Created, accountResponse.StatusCode);
        var account = (await accountResponse.Content.ReadFromJsonAsync<EmailAccountDto>())!;
        var job = (await (await client.PostAsJsonAsync($"/api/email-accounts/{account.Id}/sync-jobs", new EnqueueEmailSyncRequest(10)))
            .Content.ReadFromJsonAsync<EmailSyncJobDto>())!;
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync($"/api/email-sync-jobs/{job.Id}/dispatch", null)).StatusCode);
        Assert.Equal(1, server.Calls); Assert.NotNull(received); Assert.Equal(expectedSignature, actualSignature);
        Assert.Equal(account.Id, received.Payload.AccountRef); Assert.Null(received.Payload.PreviousCursor); Assert.Equal(10, received.Payload.MaxItems);
        await using var db = Db();
        Assert.Equal("result-received", (await db.EmailSyncJobs.AsNoTracking().SingleAsync(x => x.Id == job.Id)).StatusCode);
        Assert.Equal("acknowledged", (await db.IntegrationAttempts.SingleAsync(x => x.OperationId == job.IntegrationOperationId)).StatusCode);
        Assert.Null((await db.EmailSyncStates.AsNoTracking().SingleAsync(x => x.EmailAccountId == account.Id)).Cursor);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync($"/api/email-sync-jobs/{job.Id}/process-result", null)).StatusCode);
        Assert.Equal("http-cursor", (await db.EmailSyncStates.AsNoTracking().SingleAsync(x => x.EmailAccountId == account.Id)).Cursor);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync($"/api/email-sync-jobs/{job.Id}/dispatch", null)).StatusCode);
        Assert.Equal(1, server.Calls);
    }

    [Fact]
    public async Task DispatchRequiresHumanAndCsrfAndDisabledDefaultCreatesNoAttempt()
    {
        using var s = await Prepare(dispatch: false, receive: false);
        var path = $"/api/email-sync-jobs/{s.Job.Id}/dispatch";
        using var anonymous = s.Host.Client();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync(path, null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.SendAsync(s.Host.Signed("{}"u8.ToArray(), target: path))).StatusCode);
        s.Client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Client.PostAsync(path, null)).StatusCode);
        await SecurityTestHost.CsrfAsync(s.Client);
        var response = await s.Client.PostAsync(path, null);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode); Assert.Contains("EmailSyncTransportDisabled", await response.Content.ReadAsStringAsync());
        await AssertState(s, "queued", 0, null);
        await using var db = Db(); Assert.Empty(await db.IntegrationAttempts.Where(x => x.OperationId == s.Job.IntegrationOperationId).ToListAsync());
    }

    [Theory]
    [InlineData("")][InlineData("http://localhost/email")][InlineData("https://user@localhost/email")]
    [InlineData("https://localhost/email?target=x")][InlineData("https://localhost/email#fragment")]
    public async Task EnabledEmailTransportRejectsUnsafeConfiguration(string endpoint)
    {
        using var host = await SecurityTestHost.CreateAsync(Database);
        host.Integration.EmailSync = new() { Enabled = true, Endpoint = endpoint };
        Assert.False(host.Integration.IsValid(host.Operator));
    }

    [Fact]
    public async Task EmailTransportNeedsBothSwitchesAndRetainsTheGenericEndpoint()
    {
        using var host = await SecurityTestHost.CreateAsync(Database);
        var genericEndpoint = host.Integration.Endpoint;
        host.Integration.EmailSync = new() { Enabled = true, Endpoint = "https://localhost/email" };
        Assert.True(host.Integration.IsValid(host.Operator));
        host.Integration.Enabled = false; Assert.False(host.Integration.IsValid(host.Operator));
        host.Integration.EmailSync.Enabled = false; Assert.True(host.Integration.IsValid(host.Operator));
        Assert.Equal(genericEndpoint, host.Integration.Endpoint);
    }

    [Fact]
    public async Task MismatchedAccountFailsClosedBeforeHttp()
    {
        using var s = await Prepare(dispatch: false, receive: false);
        var calls = 0;
        await using var server = await IntegrationTlsServer.StartAsync(c => { calls++; c.Response.StatusCode = 202; return Task.CompletedTask; });
        s.Host.Integration.EmailSync = new() { Enabled = true, Endpoint = server.Endpoint.AbsoluteUri };
        await using var db = Db();
        using var client = new HttpClient(server.Handler()); using var gate = new IntegrationSendGate(Options.Create(s.Host.Integration));
        var transport = new HttpEmailSyncTransport(new(client, TimeProvider.System), gate,
            new Monitor<IntegrationOptions>(s.Host.Integration), new Monitor<ProspectionCrm.Api.Security.OperatorOptions>(s.Host.Operator), db);
        var op = await db.IntegrationOperations.SingleAsync(x => x.Id == s.Job.IntegrationOperationId);
        var forged = new EmailSyncCommand(1, op.CommandMessageId, op.Id, op.CorrelationId, "email.sync", new(Guid.NewGuid(), null, 10));
        Assert.Equal(IntegrationTransportOutcome.Failed, await transport.DispatchAsync(forged, default)); Assert.Equal(0, calls);
    }
}
