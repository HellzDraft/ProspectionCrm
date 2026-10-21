using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ProspectionCrm.Api.Data;
using ProspectionCrm.Api.Dtos.Integrations;
using ProspectionCrm.Api.Security;
using ProspectionCrm.Api.Services.Integrations;

namespace ProspectionCrm.Api.Services.Calendar;

// Only the endpoint is calendar-specific. TLS, HMAC, response bounds and outcomes use the shared transport.
public sealed class HttpCalendarSyncTransport(IntegrationTransport transport, IntegrationSendGate gate,
    IOptionsMonitor<IntegrationOptions> options, IOptionsMonitor<OperatorOptions> human, ProspectionCrmDbContext db) : ICalendarSyncTransport
{
    public bool IsAvailable => Enabled(options.CurrentValue);
    private bool Enabled(IntegrationOptions config) => config.Enabled && config.CalendarSync.Enabled && config.IsValid(human.CurrentValue);

    public async Task<IntegrationTransportOutcome> DispatchAsync(CalendarSyncCommand command, CancellationToken token)
    {
        var config = options.CurrentValue;
        if (!Enabled(config)) return IntegrationTransportOutcome.Failed;
        // Validate the complete durable command binding before any network I/O.
        if (!await db.CalendarSyncJobs.AnyAsync(x => x.Id == command.Payload.JobRef
            && x.IntegrationOperationId == command.OperationId && x.CalendarId == command.Payload.CalendarRef
            && x.WorkspaceId == config.WorkspaceId && x.ConnectionId == config.ConnectionId
            && x.OperationType == CalendarSyncContracts.Type && x.StatusCode == "running"
            && x.PreviousCursor == command.Payload.PreviousCursor && x.MaxItems == command.Payload.MaxItems
            && db.IntegrationOperations.Any(o => o.Id == x.IntegrationOperationId && o.WorkspaceId == x.WorkspaceId
                && o.ConnectionId == x.ConnectionId && o.Type == command.Type && o.Version == command.Version
                && o.CommandMessageId == command.MessageId && o.CorrelationId == command.CorrelationId)
            && db.Calendars.Any(c => c.Id == x.CalendarId && c.WorkspaceId == x.WorkspaceId
                && c.ConnectionId == x.ConnectionId && c.StatusCode == "active" && c.ArchivedAt == null
                && c.Revision == x.CalendarRevision && db.CalendarAccounts.Any(a => a.Id == c.CalendarAccountId
                    && a.WorkspaceId == c.WorkspaceId && a.ConnectionId == c.ConnectionId && a.ProviderCode == c.ProviderCode
                    && a.StatusCode == "active" && a.ArchivedAt == null && a.Revision == x.AccountRevision)), token))
            return IntegrationTransportOutcome.Failed;
        if (!await gate.EnterAsync(token)) return IntegrationTransportOutcome.Failed;
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(config.TimeoutSeconds));
            return await transport.SendAsync(config, Encoding.UTF8.GetBytes(CalendarSyncContracts.Serialize(command)),
                deadline.Token, new Uri(config.CalendarSync.Endpoint));
        }
        finally { gate.Exit(); }
    }
}
