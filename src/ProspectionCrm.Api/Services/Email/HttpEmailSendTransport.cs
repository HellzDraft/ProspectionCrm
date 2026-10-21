using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ProspectionCrm.Api.Data;
using ProspectionCrm.Api.Dtos.Integrations;
using ProspectionCrm.Api.Security;
using ProspectionCrm.Api.Services.Integrations;

namespace ProspectionCrm.Api.Services.Email;

public sealed class HttpEmailSendTransport(IntegrationTransport transport,
    IOptionsMonitor<IntegrationOptions> options, IOptionsMonitor<OperatorOptions> human,
    ProspectionCrmDbContext db) : IEmailSendTransport
{
    public bool IsAvailable => Enabled(options.CurrentValue);
    private bool Enabled(IntegrationOptions config) => config.Enabled && config.EmailSend.Enabled && config.IsValid(human.CurrentValue);

    public async Task<EmailSendTransportOutcome> DispatchAsync(EmailSendCommand command, CancellationToken token)
    {
        var config = options.CurrentValue;
        if (!Enabled(config)) return EmailSendTransportOutcome.NotSent;
        var snapshot = command.Snapshot;
        if (snapshot.WorkspaceId != config.WorkspaceId || snapshot.ConnectionId != config.ConnectionId
            || !await db.EmailSendDeliveries.AnyAsync(x => x.Id == command.DeliveryId
                && x.WorkspaceId == config.WorkspaceId && x.ConnectionId == config.ConnectionId
                && x.EmailAccountId == snapshot.EmailAccountId && x.EmailSendPlanId == snapshot.PlanId
                && x.IntegrationOperationId == command.OperationId && x.IdempotencyKey == command.IdempotencyKey
                && x.ContentHash == EmailSendSnapshots.Hash(snapshot) && x.StatusCode == "dispatching"
                && db.IntegrationOperations.Any(o => o.Id == x.IntegrationOperationId
                    && o.CommandMessageId == command.MessageId && o.CorrelationId == command.CorrelationId
                    && o.Type == "email.send"), token)) return EmailSendTransportOutcome.NotSent;
        // Dispatcher owns the concurrency gate. Never acquire it again here.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(config.TimeoutSeconds));
        var outcome = await transport.SendAsync(config, Encoding.UTF8.GetBytes(EmailSendContracts.Serialize(command)),
            deadline.Token, new Uri(config.EmailSend.Endpoint));
        // Shared Failed includes HTTP 4xx; it is not evidence of provider non-acceptance.
        return outcome == IntegrationTransportOutcome.Acknowledged
            ? EmailSendTransportOutcome.Acknowledged : EmailSendTransportOutcome.Unknown;
    }
}
