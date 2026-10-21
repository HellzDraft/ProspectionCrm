using ProspectionCrm.Api.Dtos.Integrations;
using ProspectionCrm.Api.Services.Integrations;

namespace ProspectionCrm.Api.Services.Calendar;

// No network adapter or automatic worker in Phase 9.6.1.
public interface ICalendarSyncTransport
{
    bool IsAvailable { get; }
    Task<IntegrationTransportOutcome> DispatchAsync(CalendarSyncCommand command, CancellationToken token);
}

public sealed class UnavailableCalendarSyncTransport : ICalendarSyncTransport
{
    public bool IsAvailable => false;
    public Task<IntegrationTransportOutcome> DispatchAsync(CalendarSyncCommand command, CancellationToken token) =>
        throw new CalendarSyncException("CalendarSyncTransportDisabled");
}

public sealed class CalendarSyncDispatcher(CalendarSyncService sync, ICalendarSyncTransport transport)
{
    public async Task DispatchAsync(Guid workspace, Guid job, CancellationToken token = default)
    {
        if (!transport.IsAvailable) throw new CalendarSyncException("CalendarSyncTransportDisabled");
        await IntegrationSyncDispatch.RunAsync(t => sync.BeginAsync(workspace, job, t), transport.DispatchAsync,
            (attempt, outcome, t) => sync.CompleteDispatchAsync(workspace, job, attempt, outcome, t), token);
    }
}
