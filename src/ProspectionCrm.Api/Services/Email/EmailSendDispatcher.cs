using ProspectionCrm.Api.Dtos.Integrations;
using ProspectionCrm.Api.Services.Integrations;

namespace ProspectionCrm.Api.Services.Email;

// NotSent is evidence of failure BEFORE any provider acceptance; a generic HTTP failure is Unknown.
public enum EmailSendTransportOutcome { Acknowledged, NotSent, Unknown }
public interface IEmailSendTransport
{
    bool IsAvailable { get; }
    Task<EmailSendTransportOutcome> DispatchAsync(EmailSendCommand command, CancellationToken token);
}
public sealed class UnavailableEmailSendTransport : IEmailSendTransport
{
    public bool IsAvailable => false;
    public Task<EmailSendTransportOutcome> DispatchAsync(EmailSendCommand command, CancellationToken token) =>
        throw new EmailSyncException("EmailSendTransportDisabled");
}
public sealed class EmailSendDispatcher(EmailSendDeliveryService deliveries, IEmailSendTransport transport, IntegrationSendGate gate)
{
    public async Task DispatchAsync(Guid workspace, Guid id, CancellationToken token = default)
    {
        if (!transport.IsAvailable) throw new EmailSyncException("EmailSendTransportDisabled");
        if (!await gate.EnterAsync(token)) throw new EmailSyncException("EmailSendBusy");
        try
        {
            var (command, attempt) = await deliveries.BeginAsync(workspace, id, token);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            EmailSendTransportOutcome outcome;
            try { outcome = await transport.DispatchAsync(command, deadline.Token); }
            catch (Exception) { outcome = EmailSendTransportOutcome.Unknown; }
            using var finalize = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await deliveries.CompleteAsync(workspace, id, attempt, outcome, finalize.Token);
        }
        finally { gate.Exit(); }
    }
}
