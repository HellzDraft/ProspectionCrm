using ProspectionCrm.Api.Dtos.Integrations;
using ProspectionCrm.Api.Services.Integrations;

namespace ProspectionCrm.Api.Services.Email;

// Disabled by default; an explicitly configured adapter delegates to the shared integration transport.
public interface IEmailSyncTransport
{
    bool IsAvailable { get; }
    Task<IntegrationTransportOutcome> DispatchAsync(EmailSyncCommand command, CancellationToken token);
}

public sealed class UnavailableEmailSyncTransport : IEmailSyncTransport
{
    public bool IsAvailable => false;
    public Task<IntegrationTransportOutcome> DispatchAsync(EmailSyncCommand command, CancellationToken token) =>
        throw new EmailSyncException("EmailSyncTransportDisabled");
}

public sealed class EmailSyncDispatcher(EmailSyncService sync, IEmailSyncTransport transport)
{
    public async Task DispatchAsync(Guid workspace, Guid job, CancellationToken token = default)
    {
        if (!transport.IsAvailable) throw new EmailSyncException("EmailSyncTransportDisabled");
        var (command, attempt) = await sync.BeginAsync(workspace, job, token);
        // Begin has committed; a request disconnect cannot skip recording the transport outcome.
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        IntegrationTransportOutcome outcome;
        try { outcome = await transport.DispatchAsync(command, deadline.Token); }
        catch (Exception e) when (e is IOException or HttpRequestException or OperationCanceledException)
        { outcome = IntegrationTransportOutcome.Unknown; }
        using var finalize = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await sync.CompleteDispatchAsync(workspace, job, attempt, outcome, finalize.Token);
    }
}
