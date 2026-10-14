using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ProspectionCrm.Api.Data;
using ProspectionCrm.Api.Dtos.Integrations;
using ProspectionCrm.Api.Security;

namespace ProspectionCrm.Api.Services.Integrations;

public sealed class IntegrationSendGate(IOptions<IntegrationOptions> options) : IDisposable
{
    private readonly SemaphoreSlim gate = new(Math.Clamp(options.Value.MaxConcurrentSends, 1, 8));
    public Task<bool> EnterAsync(CancellationToken token) => gate.WaitAsync(0, token);
    public void Exit() => gate.Release();
    public void Dispose() => gate.Dispose();
}

public sealed class IntegrationTransport(HttpClient client, TimeProvider clock)
{
    public static SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false, UseCookies = false, UseProxy = false, Credentials = null,
        AutomaticDecompression = DecompressionMethods.None, MaxConnectionsPerServer = 8,
        MaxResponseHeadersLength = 8, ConnectTimeout = TimeSpan.FromSeconds(10),
        PooledConnectionLifetime = TimeSpan.FromMinutes(2)
    };
    public async Task<IntegrationTransportOutcome> SendAsync(IntegrationOptions config, byte[] body, CancellationToken token)
    {
        var destination = new Uri(config.Endpoint);
        using var request = new HttpRequestMessage(HttpMethod.Post, destination);
        request.Content = new ByteArrayContent(body);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        var time = clock.GetUtcNow().ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        request.Headers.Add(IntegrationSignature.KeyHeader, config.OutgoingKey.Id);
        request.Headers.Add(IntegrationSignature.TimeHeader, time);
        request.Headers.Add(IntegrationSignature.SignatureHeader, IntegrationSignature.Sign(
            Convert.FromBase64String(config.OutgoingKey.SecretBase64), IntegrationSignature.OutgoingAudience,
            config.OutgoingKey.Id, time, "POST", destination.PathAndQuery, body));
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            if ((int)response.StatusCode >= 500) return IntegrationTransportOutcome.Unknown;
            if (response.StatusCode != HttpStatusCode.Accepted) return IntegrationTransportOutcome.Failed;
            if (response.Content.Headers.ContentEncoding.Count != 0 || response.Content.Headers.ContentLength > 4096)
                return IntegrationTransportOutcome.Unknown;
            await using var stream = await response.Content.ReadAsStreamAsync(token);
            var bytes = new byte[4097]; var count = 0;
            while (count < bytes.Length)
            {
                var read = await stream.ReadAsync(bytes.AsMemory(count), token);
                if (read == 0) return IntegrationTransportOutcome.Acknowledged;
                count += read;
            }
            return IntegrationTransportOutcome.Unknown;
        }
        catch (HttpRequestException ex) when (ex.HttpRequestError == HttpRequestError.SecureConnectionError)
        { return IntegrationTransportOutcome.Failed; }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
        { return IntegrationTransportOutcome.Unknown; }
    }
}

public sealed class IntegrationProbeService(IntegrationStore store, IntegrationTransport transport,
    IntegrationSendGate gate, IOptionsMonitor<IntegrationOptions> options, IOptionsMonitor<OperatorOptions> human,
    ProspectionCrmDbContext db, TimeProvider clock)
{
    public IntegrationOptions RequireConfiguration(Guid workspace)
    {
        var config = options.CurrentValue;
        if (!config.Enabled || !config.IsValid(human.CurrentValue)) throw new IntegrationConflictException("integration-disabled");
        if (config.WorkspaceId != workspace) throw new IntegrationAccessException();
        return config;
    }
    public async Task<IntegrationTransportOutcome> SendAsync(Guid workspace, Guid operation, long revision, CancellationToken token)
    {
        var config = RequireConfiguration(workspace);
        var op = await store.FindAsync(workspace, operation, token) ?? throw new IntegrationAccessException();
        if (op.ConnectionId != config.ConnectionId) throw new IntegrationAccessException();
        if (!await gate.EnterAsync(token)) throw new IntegrationConflictException("integration-busy");
        try
        {
            var command = Encoding.UTF8.GetBytes(IntegrationContracts.Serialize(await store.CommandAsync(workspace, operation, token)));
            var attempt = await store.BeginAttemptAsync(workspace, operation, revision, token);
            // The transaction has committed. Browser disconnect cannot abort finalization after dispatch.
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(config.TimeoutSeconds));
            var outcome = await transport.SendAsync(config, command, deadline.Token);
            using var finalize = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await store.FinishAttemptAsync(workspace, operation, attempt.Id, outcome, finalize.Token);
            // A process crash / failed finalization leaves started, never a fabricated success.
            return outcome;
        }
        finally { gate.Exit(); }
    }
    public async Task ResolveAsync(Guid workspace, Guid operation, Guid attemptId, CancellationToken token)
    {
        var attempt = await db.IntegrationAttempts.AsNoTracking().SingleOrDefaultAsync(x => x.OperationId == operation && x.Id == attemptId
            && db.IntegrationOperations.Any(op => op.Id == operation && op.WorkspaceId == workspace), token);
        if (attempt is null) throw new IntegrationAccessException();
        if (attempt.StatusCode != "started" || clock.GetUtcNow() - attempt.StartedAt < TimeSpan.FromMinutes(2))
            throw new IntegrationConflictException("attempt-not-recoverable");
        await store.FinishAttemptAsync(workspace, operation, attemptId, IntegrationTransportOutcome.Unknown, token);
    }
}
