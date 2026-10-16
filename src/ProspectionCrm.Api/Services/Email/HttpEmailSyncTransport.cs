using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ProspectionCrm.Api.Data;
using ProspectionCrm.Api.Dtos.Integrations;
using ProspectionCrm.Api.Security;
using ProspectionCrm.Api.Services.Integrations;

namespace ProspectionCrm.Api.Services.Email;

// Only the endpoint is email-specific. TLS, HMAC, response bounds and outcomes use the shared transport.
public sealed class HttpEmailSyncTransport(IntegrationTransport transport, IntegrationSendGate gate,
    IOptionsMonitor<IntegrationOptions> options, IOptionsMonitor<OperatorOptions> human, ProspectionCrmDbContext db) : IEmailSyncTransport
{
    public bool IsAvailable => Enabled(options.CurrentValue);
    private bool Enabled(IntegrationOptions config) => config.Enabled && config.EmailSync.Enabled && config.IsValid(human.CurrentValue);

    public async Task<IntegrationTransportOutcome> DispatchAsync(EmailSyncCommand command, CancellationToken token)
    {
        var config = options.CurrentValue;
        if (!Enabled(config)) return IntegrationTransportOutcome.Failed;
        // Recheck the immutable operation/account binding after Begin committed, before releasing any command.
        if (!await db.EmailSyncJobs.AnyAsync(x => x.IntegrationOperationId == command.OperationId
            && x.EmailAccountId == command.Payload.AccountRef && x.WorkspaceId == config.WorkspaceId
            && x.ConnectionId == config.ConnectionId && x.OperationType == EmailSyncContracts.Type, token))
            return IntegrationTransportOutcome.Failed;
        if (!await gate.EnterAsync(token)) return IntegrationTransportOutcome.Failed;
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(config.TimeoutSeconds));
            return await transport.SendAsync(config, Encoding.UTF8.GetBytes(EmailSyncContracts.Serialize(command)),
                deadline.Token, new Uri(config.EmailSync.Endpoint));
        }
        finally { gate.Exit(); }
    }
}
