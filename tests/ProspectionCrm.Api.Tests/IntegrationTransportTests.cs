using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using ProspectionCrm.Api.Dtos.Integrations;
using ProspectionCrm.Api.Entities;
using ProspectionCrm.Api.Services.Integrations;
using Xunit;

namespace ProspectionCrm.Api.Tests;

public sealed class IntegrationTransportTests(AutomationJobDatabase database) : IClassFixture<AutomationJobDatabase>
{
    [Fact]
    public async Task IsolatedTestRootUsesRealChainValidation()
    {
        await using var server = await IntegrationTlsServer.StartAsync(c => { c.Response.StatusCode = 202; return Task.CompletedTask; });
        using var handler = server.Handler();
        Assert.Null(handler.SslOptions.RemoteCertificateValidationCallback);
        Assert.False(handler.AllowAutoRedirect); Assert.False(handler.UseCookies); Assert.False(handler.UseProxy);
        using var client = new HttpClient(handler);
        Assert.Equal(HttpStatusCode.Accepted, (await client.GetAsync(server.Endpoint)).StatusCode);
    }
    [Fact]
    public async Task AuthenticatedOperatorToRealTlsServerSignedCallbackBeforeAckThenDurableProcessing()
    {
        using var host = await SecurityTestHost.CreateAsync(database);
        HttpClient machine = null!;
        await using var server = await IntegrationTlsServer.StartAsync(async context =>
        {
            Assert.True(context.Request.IsHttps); Assert.False(context.Request.Headers.ContainsKey("Cookie"));
            using var buffer = new MemoryStream(); await context.Request.Body.CopyToAsync(buffer); var body = buffer.ToArray();
            var command = IntegrationContracts.ParseCommand(Encoding.UTF8.GetString(body));
            var expected = IntegrationSignature.Sign(Convert.FromBase64String(host.Integration.OutgoingKey.SecretBase64),
                IntegrationSignature.OutgoingAudience, host.Integration.OutgoingKey.Id, context.Request.Headers[IntegrationSignature.TimeHeader]!, "POST", "/probe", body);
            Assert.Equal(expected, context.Request.Headers[IntegrationSignature.SignatureHeader]);
            var result = new IntegrationResultEnvelope(1, Guid.NewGuid(), command.OperationId, command.CorrelationId,
                command.MessageId, command.Type, new(command.Payload.ProbeId, "succeeded"));
            var callback = await machine.SendAsync(host.Signed(Encoding.UTF8.GetBytes(IntegrationContracts.Serialize(result))));
            Assert.Equal(HttpStatusCode.Accepted, callback.StatusCode);
            await using var db = database.Db();
            Assert.Equal("started", (await db.IntegrationAttempts.SingleAsync(a => a.OperationId == command.OperationId)).StatusCode);
            Assert.Equal("result-received", (await db.IntegrationOperations.SingleAsync(o => o.Id == command.OperationId)).StatusCode);
            context.Response.StatusCode = 202;
        });
        host.Integration.Endpoint = server.Endpoint.AbsoluteUri; host.Integration.TimeoutSeconds = 10;
        host.TransportHandler = () => server.Handler();
        using var human = host.Client(); machine = host.Client(); using var machineLifetime = machine;
        await SecurityTestHost.LoginAsync(human);
        var created = await human.PostAsync("/api/integrations/probes", null);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var op = (await created.Content.ReadFromJsonAsync<IntegrationOperation>())!;
        var sent = await human.PostAsJsonAsync($"/api/integrations/probes/{op.Id}/send", new { revision = 0 });
        Assert.Equal(HttpStatusCode.OK, sent.StatusCode);
        Assert.Equal("acknowledged", (await sent.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("transport").GetString());
        Assert.Equal(1, server.Calls);
        await using (var db = database.Db())
        {
            Assert.Equal("result-received", (await db.IntegrationOperations.SingleAsync(x => x.Id == op.Id)).StatusCode);
            Assert.Null((await db.IntegrationReceipts.SingleAsync(x => x.OperationId == op.Id)).ProcessedAt);
        }
        // A fresh HTTP request / DB scope processes the receipt; there is no second network send.
        Assert.Equal(HttpStatusCode.NoContent, (await human.PostAsJsonAsync($"/api/integrations/probes/{op.Id}/process", new { revision = 2 })).StatusCode);
        await using var verify = database.Db();
        Assert.Equal("succeeded", (await verify.IntegrationOperations.SingleAsync(x => x.Id == op.Id)).StatusCode);
        Assert.Equal("acknowledged", (await verify.IntegrationAttempts.SingleAsync(x => x.OperationId == op.Id)).StatusCode);
    }

    [Theory]
    [InlineData("ack", "acknowledged")][InlineData("timeout", "unknown")][InlineData("redirect", "failed")]
    [InlineData("large", "unknown")][InlineData("server-error", "unknown")][InlineData("rejected", "failed")]
    public async Task RealTlsTransportOutcomesAreBoundedAndNeverImplyLogicalSuccess(string mode, string expected)
    {
        await using var redirectTarget = await IntegrationTlsServer.StartAsync(c => { c.Response.StatusCode = 202; return Task.CompletedTask; });
        await using var server = await IntegrationTlsServer.StartAsync(async c =>
        {
            if (mode == "timeout") { await Task.Delay(3000, c.RequestAborted); return; }
            if (mode == "redirect") { c.Response.StatusCode = 307; c.Response.Headers.Location = redirectTarget.Endpoint.AbsoluteUri; return; }
            c.Response.StatusCode = mode switch { "server-error" => 500, "rejected" => 400, _ => 202 };
            if (mode == "large") await c.Response.WriteAsync(new string('x', 4097));
        });
        using var host = await SecurityTestHost.CreateAsync(database);
        host.Integration.Endpoint = server.Endpoint.AbsoluteUri; host.TransportHandler = () => server.Handler();
        using var client = host.Client(); await SecurityTestHost.LoginAsync(client);
        var op = (await (await client.PostAsync("/api/integrations/probes", null)).Content.ReadFromJsonAsync<IntegrationOperation>())!;
        var response = await client.PostAsJsonAsync($"/api/integrations/probes/{op.Id}/send", new { revision = 0 });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(expected, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("transport").GetString());
        await using var db = database.Db();
        Assert.Equal(expected, (await db.IntegrationAttempts.SingleAsync(a => a.OperationId == op.Id)).StatusCode);
        Assert.Equal("awaiting-result", (await db.IntegrationOperations.SingleAsync(o => o.Id == op.Id)).StatusCode);
        Assert.Equal(1, server.Calls); Assert.Equal(0, redirectTarget.Calls);
    }

    [Theory]
    [InlineData("untrusted")][InlineData("wrong-name")][InlineData("expired")]
    public async Task RealTlsRejectsInvalidCertificatesBeforeSendingCredentials(string kind)
    {
        await using var server = await IntegrationTlsServer.StartAsync(c => { c.Response.StatusCode = 202; return Task.CompletedTask; },
            kind == "wrong-name" ? "wrong.invalid" : "localhost", kind == "expired");
        using var host = await SecurityTestHost.CreateAsync(database); host.Integration.Endpoint = server.Endpoint.AbsoluteUri;
        host.TransportHandler = () => server.Handler(kind != "untrusted");
        using var client = host.Client(); await SecurityTestHost.LoginAsync(client);
        var op = (await (await client.PostAsync("/api/integrations/probes", null)).Content.ReadFromJsonAsync<IntegrationOperation>())!;
        var result = await client.PostAsJsonAsync($"/api/integrations/probes/{op.Id}/send", new { revision = 0 });
        Assert.Equal("failed", (await result.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("transport").GetString());
        Assert.Equal(0, server.Calls);
    }

    [Fact]
    public async Task DisabledTransportDoesNotCreateAttemptOrCallNetwork()
    {
        using var host = await SecurityTestHost.CreateAsync(database); host.Integration.Enabled = false;
        var calls = 0; host.TransportHandler = () => new SimulatedHandler((_, _) => { calls++; throw new InvalidOperationException("must-not-send"); });
        using var client = host.Client(); await SecurityTestHost.LoginAsync(client);
        await using var db = database.Db();
        var op = await new IntegrationStore(db, host.Clock).CreateProbeAsync(host.Operator.WorkspaceId, host.Integration.ConnectionId);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"/api/integrations/probes/{op.Id}/send", new { revision = 0 })).StatusCode);
        Assert.Empty(await db.IntegrationAttempts.Where(x => x.OperationId == op.Id).ToListAsync()); Assert.Equal(0, calls);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync("/api/integrations/probes", null)).StatusCode);
    }

    [Fact]
    public async Task SimulatedDisconnectStillFinalizesAndStartedRecoveryIsExplicitAndDelayed()
    {
        using var host = await SecurityTestHost.CreateAsync(database);
        var departed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Integration.TimeoutSeconds = 5;
        host.TransportHandler = () => new SimulatedHandler(async (_, token) =>
        { departed.TrySetResult(); await release.Task.WaitAsync(token); return new(HttpStatusCode.Accepted); });
        using var client = host.Client(); await SecurityTestHost.LoginAsync(client);
        var op = (await (await client.PostAsync("/api/integrations/probes", null)).Content.ReadFromJsonAsync<IntegrationOperation>())!;
        using var cancel = new CancellationTokenSource();
        var send = client.PostAsJsonAsync($"/api/integrations/probes/{op.Id}/send", new { revision = 0 }, cancel.Token);
        await departed.Task.WaitAsync(TimeSpan.FromSeconds(10)); cancel.Cancel(); release.TrySetResult();
        try { await send; } catch (OperationCanceledException) { }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var db = database.Db();
        while (!await db.IntegrationAttempts.AnyAsync(x => x.OperationId == op.Id && x.StatusCode == "acknowledged", timeout.Token)) await Task.Delay(20, timeout.Token);
        var store = new IntegrationStore(db, host.Clock); var interrupted = await store.CreateProbeAsync(op.WorkspaceId, host.Integration.ConnectionId);
        var attempt = await store.BeginAttemptAsync(op.WorkspaceId, interrupted.Id, 0);
        var route = $"/api/integrations/probes/{interrupted.Id}/attempts/{attempt.Id}/resolve-unknown";
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync(route, null)).StatusCode);
        host.Clock.Now += TimeSpan.FromMinutes(3);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync(route, null)).StatusCode);
        Assert.Equal("unknown", (await db.IntegrationAttempts.SingleAsync(x => x.Id == attempt.Id)).StatusCode);
        Assert.Equal(1, await db.IntegrationAttempts.CountAsync(x => x.OperationId == interrupted.Id));
    }

    [Fact]
    public async Task SimulatedConcurrentSendsAreBoundedBeforePersistingAnotherAttempt()
    {
        using var host = await SecurityTestHost.CreateAsync(database); host.Integration.MaxConcurrentSends = 1; host.Integration.TimeoutSeconds = 10;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.TransportHandler = () => new SimulatedHandler(async (_, token) =>
        { entered.TrySetResult(); await release.Task.WaitAsync(token); return new(HttpStatusCode.Accepted); });
        using var client = host.Client(); await SecurityTestHost.LoginAsync(client);
        var first = (await (await client.PostAsync("/api/integrations/probes", null)).Content.ReadFromJsonAsync<IntegrationOperation>())!;
        var second = (await (await client.PostAsync("/api/integrations/probes", null)).Content.ReadFromJsonAsync<IntegrationOperation>())!;
        var sending = client.PostAsJsonAsync($"/api/integrations/probes/{first.Id}/send", new { revision = 0 });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"/api/integrations/probes/{second.Id}/send", new { revision = 0 })).StatusCode);
            await using var db = database.Db(); Assert.False(await db.IntegrationAttempts.AnyAsync(x => x.OperationId == second.Id));
        }
        finally { release.TrySetResult(); }
        Assert.Equal(HttpStatusCode.OK, (await sending).StatusCode);
    }
    private sealed class SimulatedHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token); }
}
