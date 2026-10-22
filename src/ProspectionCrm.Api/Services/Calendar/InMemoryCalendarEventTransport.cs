using System.Globalization;
using ProspectionCrm.Api.Dtos.Integrations;

namespace ProspectionCrm.Api.Services.Calendar;

// Explicitly injected in tests only. No network, credential, invitation or notification facility.
// This ledger is intentionally volatile; only CRM evidence/attempts survive a process restart.
public sealed class InMemoryCalendarEventTransport : ICalendarEventTransport
{
    private readonly object mutex = new();
    private readonly Dictionary<(Guid Connection, Guid Calendar, string ExternalCalendar, string Provider, string Event), InboundCalendarEvent> events = [];
    private readonly Dictionary<string, (string Command, CalendarEventResult Result)> ledger = [];
    public bool IsAvailable => true;
    public int EffectCount { get; private set; }
    public void Seed(Guid connection, Guid calendar, string externalCalendar, string provider, InboundCalendarEvent value)
    { lock (mutex) events[(connection, calendar, externalCalendar, provider, value.ExternalEventId)] = value; }
    public CalendarEventResult? FindProof(string key)
    { lock (mutex) return ledger.TryGetValue(key, out var value) ? value.Result : null; }
    public Task<CalendarEventTransportOutcome> DispatchAsync(CalendarEventCommand command, CancellationToken token)
    {
        var canonical = CalendarEventContracts.Serialize(command);
        token.ThrowIfCancellationRequested();
        lock (mutex)
        {
            if (ledger.TryGetValue(command.IdempotencyKey, out var prior))
            {
                if (prior.Command != canonical) throw new CalendarSyncException("CalendarSimulatorKeyConflict");
                return Task.FromResult(CalendarEventTransportOutcome.Acknowledged);
            }
            var s = command.Snapshot; var c = s.Content; var p = command.Precondition;
            string outcome = "applied"; long? observed = null; string? diagnostic = null; InboundCalendarEvent? applied = null;
            InboundCalendarEvent? old = null;
            if (p is not null)
            {
                events.TryGetValue((s.ConnectionId, c.CalendarId, command.ExternalCalendarId, command.ProviderCode, p.ExternalEventId), out old);
                if (old is null) { outcome = "not-applied"; diagnostic = "TargetMissing"; }
                else if (old.Revision != p.ExpectedRevision) { outcome = "version-conflict"; observed = old.Revision; diagnostic = "VersionMismatch"; }
                else if (InboundCalendarImportService.Hash(old) != p.TargetContentHash || old.Status != "confirmed")
                { outcome = "not-applied"; diagnostic = "TargetContentMismatch"; }
            }
            if (outcome == "applied")
            {
                applied = new(old?.ExternalEventId ?? $"simulated-event-{Guid.NewGuid():N}", old is null ? 1 : checked(old.Revision + 1),
                    c.Title, c.Description, c.Location, DateTimeOffset.Parse(c.StartsAt, CultureInfo.InvariantCulture),
                    DateTimeOffset.Parse(c.EndsAt, CultureInfo.InvariantCulture), c.IsAllDay, c.TimeZoneId, "confirmed", old?.SeriesId, old?.OccurrenceId);
            }
            var result = new CalendarEventResult(1, Guid.NewGuid(), command.OperationId, command.CorrelationId, command.MessageId,
                command.Type, command.DeliveryId, command.IdempotencyKey, command.ContentHash, c.CalendarId, command.ExternalCalendarId,
                command.ProviderCode, outcome, DateTimeOffset.UtcNow, applied, observed, diagnostic);
            result = CalendarEventContracts.ParseResult(CalendarEventContracts.Serialize(result));
            // Compare, effect and idempotency ledger are protected by the same provider lock.
            if (applied is not null)
            { events[(s.ConnectionId, c.CalendarId, command.ExternalCalendarId, command.ProviderCode, applied.ExternalEventId)] = result.Event!; EffectCount++; }
            ledger.Add(command.IdempotencyKey, (canonical, result));
            return Task.FromResult(CalendarEventTransportOutcome.Acknowledged);
        }
    }
}
