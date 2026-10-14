using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ProspectionCrm.Api.Dtos.Integrations;
using ProspectionCrm.Api.Entities;
using ProspectionCrm.Api.Services.Integrations;
using Xunit;

namespace ProspectionCrm.Api.Tests;

public sealed class IntegrationSecurityTests(AutomationJobDatabase database) : IClassFixture<AutomationJobDatabase>
{
    [Fact]
    public void SignatureMatchesIndependentDeterministicVectorAndBindsEveryField()
    {
        var key = Enumerable.Range(0, 32).Select(x => (byte)x).ToArray();
        var expected = "5c6e8b91b62ac15358e704d5fa951f3c6feeb58079265db3b42890fdd2645e3d";
        string Sign(string audience = "n8n-to-crm", string keyId = "vector-1", string time = "1791936000", string method = "POST",
            string target = "/api/integrations/results", string body = "{}") => IntegrationSignature.Sign(key, audience, keyId, time, method, target, Encoding.UTF8.GetBytes(body));
        Assert.Equal(expected, Sign()); Assert.NotEqual(expected, Sign(audience: "crm-to-n8n"));
        Assert.NotEqual(expected, Sign(keyId: "vector-2")); Assert.NotEqual(expected, Sign(time: "1791936001"));
        Assert.NotEqual(expected, Sign(method: "GET")); Assert.NotEqual(expected, Sign(target: "/other")); Assert.NotEqual(expected, Sign(body: "{ }"));
    }

    [Theory]
    [InlineData("http")][InlineData("credentials")][InlineData("query")][InlineData("fragment")]
    [InlineData("same-key")][InlineData("no-human")][InlineData("duplicate-key")][InlineData("timeout")][InlineData("concurrency")]
    public async Task InvalidConfigurationCannotActivateTransport(string change)
    {
        using var host = await SecurityTestHost.CreateAsync(database);
        switch (change)
        {
            case "http": host.Integration.Endpoint = "http://localhost/probe"; break;
            case "credentials": host.Integration.Endpoint = "https://user:password@localhost/probe"; break;
            case "query": host.Integration.Endpoint += "?target=external"; break;
            case "fragment": host.Integration.Endpoint += "#fragment"; break;
            case "same-key": host.Integration.OutgoingKey.SecretBase64 = host.Integration.IncomingKeys[0].SecretBase64; break;
            case "no-human": host.ConfigureOperator = false; break;
            case "duplicate-key": host.Integration.IncomingKeys.Add(host.Integration.IncomingKeys[0]); break;
            case "timeout": host.Integration.TimeoutSeconds = 61; break;
            case "concurrency": host.Integration.MaxConcurrentSends = 9; break;
        }
        Assert.Throws<OptionsValidationException>(() => host.Client());
    }
    private async Task<(IntegrationOperation Op, byte[] Body)> Probe(SecurityTestHost host)
    {
        await using var db = database.Db(); var store = new IntegrationStore(db, host.Clock);
        var op = await store.CreateProbeAsync(host.Operator.WorkspaceId, host.Integration.ConnectionId);
        await store.BeginAttemptAsync(op.WorkspaceId, op.Id, 0);
        var result = new IntegrationResultEnvelope(1, Guid.NewGuid(), op.Id, op.CorrelationId, op.CommandMessageId, op.Type, new(op.ProbeId, "succeeded"));
        return (op, Encoding.UTF8.GetBytes(IntegrationContracts.Serialize(result)));
    }

    [Theory]
    [InlineData("missing", 401)][InlineData("invalid", 401)][InlineData("expired", 401)][InlineData("future", 401)]
    [InlineData("revoked", 401)][InlineData("changed-body", 401)][InlineData("duplicate-key", 401)]
    [InlineData("duplicate-time", 401)][InlineData("duplicate-signature", 401)][InlineData("comma-key", 401)]
    [InlineData("query", 401)][InlineData("encoding", 415)][InlineData("charset", 415)]
    [InlineData("oversize", 413)][InlineData("chunked-oversize", 413)][InlineData("invalid-utf8", 400)]
    [InlineData("invalid-json", 400)]
    public async Task RejectsInvalidMachineRequestsBeforePersistence(string invalid, int expected)
    {
        using var host = await SecurityTestHost.CreateAsync(database); using var client = host.Client();
        var (op, body) = await Probe(host);
        if (invalid is "oversize" or "chunked-oversize") body = new byte[4097];
        if (invalid == "invalid-utf8") body = [0xff, 0xfe];
        if (invalid == "invalid-json") body = "{\"unexpected\":1}"u8.ToArray();
        using var request = host.Signed(body, invalid == "revoked" ? "revoked" : "in-1",
            host.Clock.GetUtcNow().ToUnixTimeSeconds() + (invalid == "expired" ? -301 : invalid == "future" ? 301 : 0),
            invalid == "query" ? IntegrationSignature.ResultTarget + "?x=1" : IntegrationSignature.ResultTarget);
        if (invalid is "missing" or "invalid") request.Headers.Remove(IntegrationSignature.SignatureHeader);
        if (invalid == "invalid") request.Headers.Add(IntegrationSignature.SignatureHeader, new string('0', 64));
        if (invalid == "changed-body") { request.Content = new ByteArrayContent([.. body, 32]); request.Content.Headers.ContentType = new("application/json"); }
        if (invalid.StartsWith("duplicate-")) request.Headers.TryAddWithoutValidation(invalid switch
        { "duplicate-key" => IntegrationSignature.KeyHeader, "duplicate-time" => IntegrationSignature.TimeHeader, _ => IntegrationSignature.SignatureHeader }, "ambiguous");
        if (invalid == "comma-key") { request.Headers.Remove(IntegrationSignature.KeyHeader); request.Headers.TryAddWithoutValidation(IntegrationSignature.KeyHeader, "in-1,in-1"); }
        if (invalid == "encoding") request.Content!.Headers.ContentEncoding.Add("gzip");
        if (invalid == "charset") request.Content!.Headers.ContentType!.CharSet = "utf-16";
        if (invalid == "chunked-oversize")
        {
            request.Content = new StreamContent(new NonSeekable(body)); request.Content.Headers.ContentType = new("application/json");
            request.Headers.TransferEncodingChunked = true;
        }
        Assert.Equal(expected, (int)(await client.SendAsync(request)).StatusCode);
        await using var db = database.Db();
        Assert.False(await db.IntegrationReceipts.AnyAsync(x => x.OperationId == op.Id));
        Assert.Equal(1, (await db.IntegrationOperations.SingleAsync(x => x.Id == op.Id)).Revision);
    }

    [Theory]
    [InlineData("wrong-workspace")][InlineData("wrong-connection")]
    public async Task UnauthorizedKeysCannotReadOrMutateInitialDuplicateOrConflictBranches(string key)
    {
        using var host = await SecurityTestHost.CreateAsync(database); using var client = host.Client();
        var (op, body) = await Probe(host);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(host.Signed(body, key))).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await client.SendAsync(host.Signed(body))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(host.Signed(body, key))).StatusCode);
        var changed = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(body).Replace("succeeded", "failed"));
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(host.Signed(changed, key))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.SendAsync(host.Signed(changed))).StatusCode);
        await using var db = database.Db(); Assert.Equal(1, await db.IntegrationReceipts.CountAsync(x => x.OperationId == op.Id));
        Assert.Equal(2, (await db.IntegrationOperations.SingleAsync(x => x.Id == op.Id)).Revision);
    }

    [Fact]
    public async Task ConcurrentHttpDuplicatesAreDurableAndCanBeProcessedInNewRequestWithoutReplay()
    {
        using var host = await SecurityTestHost.CreateAsync(database); using var machine = host.Client();
        var (op, body) = await Probe(host);
        var replies = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => machine.SendAsync(host.Signed(body))));
        Assert.All(replies, reply => Assert.Equal(HttpStatusCode.Accepted, reply.StatusCode));
        var contents = await Task.WhenAll(replies.Select(r => r.Content.ReadFromJsonAsync<ReceiptAck>()));
        Assert.Single(contents, c => !c!.AlreadyRecorded);
        using var human = host.Client(); await SecurityTestHost.LoginAsync(human);
        Assert.Equal(HttpStatusCode.OK, (await human.GetAsync("/api/integrations/pending-receipts")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await human.PostAsJsonAsync($"/api/integrations/probes/{op.Id}/process", new { revision = 2 })).StatusCode);
        await using var db = database.Db();
        Assert.Equal("succeeded", (await db.IntegrationOperations.SingleAsync(x => x.Id == op.Id)).StatusCode);
        Assert.NotNull((await db.IntegrationReceipts.SingleAsync(x => x.OperationId == op.Id)).ProcessedAt);
        Assert.True((await (await machine.SendAsync(host.Signed(body))).Content.ReadFromJsonAsync<ReceiptAck>())!.AlreadyRecorded);
        host.Clock.Now += TimeSpan.FromSeconds(301);
        Assert.Equal(HttpStatusCode.Unauthorized, (await machine.SendAsync(host.Signed(body, time: host.Clock.GetUtcNow().ToUnixTimeSeconds() - 301))).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await machine.SendAsync(host.Signed(body))).StatusCode);
    }

    [Fact]
    public async Task RotationPreservesLogicalConnectionAndRevocationAppliesImmediately()
    {
        using var host = await SecurityTestHost.CreateAsync(database);
        host.Integration.IncomingKeys.Add(SecurityTestHost.Key("in-2", 46, host.Operator.WorkspaceId, host.Integration.ConnectionId));
        using var client = host.Client(); var (op, body) = await Probe(host);
        Assert.Equal(HttpStatusCode.Accepted, (await client.SendAsync(host.Signed(body))).StatusCode);
        host.Services.GetRequiredService<IConfiguration>()["Integrations:IncomingKeys:0:Enabled"] = "false";
        host.Services.GetRequiredService<IOptionsMonitorCache<IntegrationOptions>>().TryRemove(Options.DefaultName);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(host.Signed(body))).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await client.SendAsync(host.Signed(body, "in-2"))).StatusCode);
        await using var db = database.Db(); Assert.Equal(2, (await db.IntegrationOperations.SingleAsync(x => x.Id == op.Id)).Revision);
    }

    [Fact]
    public async Task RealKestrelTlsReceptionChecksRawTargetAndChunkedBodyLimit()
    {
        await using var certificates = await IntegrationTlsServer.StartAsync(c => { c.Response.StatusCode = 404; return Task.CompletedTask; });
        using var host = await SecurityTestHost.CreateAsync(database);
        host.UseKestrel(options => options.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(certificates.Certificate)));
        host.StartServer();
        var address = host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using var client = new HttpClient(certificates.Handler()) { BaseAddress = new Uri(address) };
        var (op, body) = await Probe(host);
        Assert.Equal(HttpStatusCode.Accepted, (await client.SendAsync(host.Signed(body))).StatusCode);
        foreach (var target in new[] { IntegrationSignature.ResultTarget + "?x=1", "/api/Integrations/results", IntegrationSignature.ResultTarget + "/" })
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(host.Signed(body, target: target))).StatusCode);
        var large = new byte[4097]; using var request = host.Signed(large);
        request.Content = new StreamContent(new NonSeekable(large)); request.Content.Headers.ContentType = new("application/json");
        request.Headers.TransferEncodingChunked = true;
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await client.SendAsync(request)).StatusCode);
        await using var db = database.Db(); Assert.Single(await db.IntegrationReceipts.Where(x => x.OperationId == op.Id).ToListAsync());
    }

    private sealed record ReceiptAck(string Status, bool AlreadyRecorded);
    private sealed class NonSeekable(byte[] body) : MemoryStream(body)
    { public override bool CanSeek => false; }
}
