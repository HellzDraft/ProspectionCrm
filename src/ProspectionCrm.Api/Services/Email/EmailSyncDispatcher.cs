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
        await IntegrationSyncDispatch.RunAsync(t => sync.BeginAsync(workspace, job, t), transport.DispatchAsync,
            (attempt, outcome, t) => sync.CompleteDispatchAsync(workspace, job, attempt, outcome, t), token);
    }
}
