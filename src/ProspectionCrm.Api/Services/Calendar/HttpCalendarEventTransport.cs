using System.Text;
using Microsoft.Extensions.Options;
using ProspectionCrm.Api.Dtos.Integrations;
using ProspectionCrm.Api.Security;
using ProspectionCrm.Api.Services.Integrations;

namespace ProspectionCrm.Api.Services.Calendar;

public sealed class HttpCalendarEventTransport(IntegrationTransport transport,
    IOptionsMonitor<IntegrationOptions> options, IOptionsMonitor<OperatorOptions> human,
    CalendarEventDeliveryService deliveries) : ICalendarEventTransport
{
    public bool IsAvailable => Enabled(options.CurrentValue);
    private bool Enabled(IntegrationOptions config) => config.Enabled && config.CalendarEvent.Enabled && config.IsValid(human.CurrentValue);

    public async Task<CalendarEventTransportOutcome> DispatchAsync(CalendarEventCommand command, CancellationToken token)
    {
        var config = options.CurrentValue;
        if (!Enabled(config)) return CalendarEventTransportOutcome.Unknown;
        byte[] body;
        try
        {
            body = Encoding.UTF8.GetBytes(CalendarEventContracts.Serialize(command));
            await deliveries.ValidateTransportAsync(command, config.WorkspaceId, config.ConnectionId, token);
        }
        catch (Exception e) when (e is CalendarSyncException or IntegrationContractException)
        { return CalendarEventTransportOutcome.Unknown; }
        // CalendarEventDispatcher owns IntegrationSendGate; never acquire it a second time.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(config.TimeoutSeconds));
        var outcome = await transport.SendAsync(config, body, deadline.Token, new Uri(config.CalendarEvent.Endpoint));
        // Even a 4xx, TLS error or incomplete 202 is not a provider proof of non-application.
        return outcome == IntegrationTransportOutcome.Acknowledged
            ? CalendarEventTransportOutcome.Acknowledged : CalendarEventTransportOutcome.Unknown;
    }
}
