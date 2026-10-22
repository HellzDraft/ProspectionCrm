using ProspectionCrm.Api.Dtos.Integrations;
using ProspectionCrm.Api.Services.Integrations;

namespace ProspectionCrm.Api.Services.Calendar;

// No HTTP status is a proof of non-application or version conflict.
public enum CalendarEventTransportOutcome { Acknowledged, Unknown }
public interface ICalendarEventTransport
{
    bool IsAvailable { get; }
    Task<CalendarEventTransportOutcome> DispatchAsync(CalendarEventCommand command, CancellationToken token);
}
public sealed class UnavailableCalendarEventTransport : ICalendarEventTransport
{
    public bool IsAvailable => false;
    public Task<CalendarEventTransportOutcome> DispatchAsync(CalendarEventCommand command, CancellationToken token) =>
        throw new CalendarSyncException("CalendarEventTransportDisabled");
}
public sealed class CalendarEventDispatcher(CalendarEventDeliveryService deliveries, ICalendarEventTransport transport, IntegrationSendGate gate)
{
    public async Task DispatchAsync(Guid workspace, Guid id, CancellationToken token = default)
    {
        if (!transport.IsAvailable) throw new CalendarSyncException("CalendarEventTransportDisabled");
        if (!await gate.EnterAsync(token)) throw new CalendarSyncException("CalendarEventBusy");
        try
        {
            var (command, attempt) = await deliveries.BeginAsync(workspace, id, token);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            CalendarEventTransportOutcome outcome;
            try { outcome = await transport.DispatchAsync(command, deadline.Token); }
            catch (Exception) { outcome = CalendarEventTransportOutcome.Unknown; }
            using var finalize = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await deliveries.CompleteAsync(workspace, id, attempt, outcome, finalize.Token);
        }
        finally { gate.Exit(); }
    }
}
