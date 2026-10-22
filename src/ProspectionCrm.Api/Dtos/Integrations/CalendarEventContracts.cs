using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ProspectionCrm.Api.Dtos.CalendarEvents;
using ProspectionCrm.Api.Services.Calendar;

namespace ProspectionCrm.Api.Dtos.Integrations;

public sealed record CalendarEventPrecondition(Guid CalendarId, string ExternalCalendarId, string ExternalEventId,
    long ExpectedRevision, string TargetContentHash);
public sealed record CalendarEventCommand(int Version, Guid MessageId, Guid OperationId, Guid CorrelationId,
    string Type, Guid DeliveryId, string IdempotencyKey, string ContentHash, string ProviderCode,
    string ExternalCalendarId, CalendarEventSnapshot Snapshot, CalendarEventPrecondition? Precondition, string Notifications);
public sealed record CalendarEventResult(int Version, Guid MessageId, Guid OperationId, Guid CorrelationId,
    Guid CommandMessageId, string Type, Guid DeliveryId, string IdempotencyKey, string ContentHash,
    Guid CalendarId, string ExternalCalendarId, string ProviderCode, string Outcome, DateTimeOffset? ProvenAt,
    InboundCalendarEvent? Event, long? ObservedRevision, string? DiagnosticCode);

public static class CalendarEventContracts
{
    public const int MaxBytes = 65536;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true, MaxDepth = 6
    };
    public static string Serialize(CalendarEventCommand value) => JsonSerializer.Serialize(ParseCommand(JsonSerializer.Serialize(value, Json)), Json);
    public static string Serialize(CalendarEventResult value) => JsonSerializer.Serialize(ParseResult(JsonSerializer.Serialize(value, Json)), Json);
    public static CalendarEventCommand ParseCommand(string json) => Read(json, root =>
    {
        var c = root.Deserialize<CalendarEventCommand>(Json) ?? throw new IntegrationContractException();
        Header(c.Version, c.MessageId, c.OperationId, c.CorrelationId, c.Type, c.DeliveryId, c.IdempotencyKey, c.ContentHash, c.ProviderCode, c.ExternalCalendarId);
        var s = CalendarEventSnapshots.Parse(root.GetProperty("snapshot").GetRawText());
        if (s is null || CalendarEventSnapshots.Hash(s) != c.ContentHash || c.Type != "calendar.event." + s.Content.OperationCode
            || c.Notifications != "disabled") throw new IntegrationContractException();
        if (s.Target is { } t)
        {
            var expected = new CalendarEventPrecondition(s.Content.CalendarId, c.ExternalCalendarId, t.ExternalEventId, t.Revision, t.ContentHash);
            if (c.Precondition != expected || t.ProviderCode != c.ProviderCode) throw new IntegrationContractException();
            var target = new InboundCalendarEvent(t.ExternalEventId, t.Revision, t.Title, t.Description, t.Location,
                t.StartsAt, t.EndsAt, t.IsAllDay, t.TimeZoneId, t.StatusCode, t.SeriesId, t.OccurrenceId);
            if (InboundCalendarImportService.Hash(target) != t.ContentHash) throw new IntegrationContractException();
        }
        else if (c.Precondition is not null) throw new IntegrationContractException();
        return c;
    });
    public static CalendarEventResult ParseResult(string json) => Read(json, root =>
    {
        var r = root.Deserialize<CalendarEventResult>(Json) ?? throw new IntegrationContractException();
        Header(r.Version, r.MessageId, r.OperationId, r.CorrelationId, r.Type, r.DeliveryId, r.IdempotencyKey, r.ContentHash, r.ProviderCode, r.ExternalCalendarId);
        if (r.CommandMessageId == Guid.Empty || r.CalendarId == Guid.Empty || r.Outcome is not ("applied" or "not-applied" or "version-conflict" or "unknown")
            || r.DiagnosticCode is not null && !Text(r.DiagnosticCode, 64)) throw new IntegrationContractException();
        if (r.Outcome == "unknown")
        {
            if (r.ProvenAt is not null || r.Event is not null || r.ObservedRevision is not null) throw new IntegrationContractException();
        }
        else
        {
            var raw = root.GetProperty("provenAt").GetString();
            if (r.ProvenAt is null || r.ProvenAt.Value.Offset != TimeSpan.Zero || raw is null
                || !(raw.EndsWith('Z') || raw.EndsWith("+00:00", StringComparison.Ordinal))) throw new IntegrationContractException();
            if (r.Outcome == "applied")
            {
                if (r.Event is null || r.ObservedRevision is not null) throw new IntegrationContractException();
                // Reuse the inbound contract on the raw JSON to retain offset, DST and precision validation.
                var envelope = JsonSerializer.Serialize(new { version = 1, messageId = r.MessageId, operationId = r.OperationId,
                    correlationId = r.CorrelationId, commandMessageId = r.CommandMessageId, type = CalendarSyncContracts.Type,
                    payload = new { calendarRef = r.CalendarId, jobRef = r.DeliveryId, outcome = "succeeded", nextCursor = (string?)null,
                        events = new[] { root.GetProperty("event") } } }, Json);
                var e = CalendarSyncContracts.ParseResult(envelope).Payload.Events.Single();
                if (e.Status != "confirmed") throw new IntegrationContractException();
                r = r with { Event = e };
            }
            else if (r.Event is not null || (r.Outcome == "version-conflict"
                ? r.Type != CalendarEventSnapshots.UpdateAction || r.ObservedRevision is null or < 0
                : r.ObservedRevision is not null)) throw new IntegrationContractException();
        }
        return r;
    });
    public static bool IsCalendarEvent(string json)
    {
        try { using var doc = JsonDocument.Parse(json, new() { MaxDepth = 6 });
            return doc.RootElement.TryGetProperty("type", out var p) && CalendarEventSnapshots.IsAction(p.GetString()); }
        catch (Exception e) when (e is JsonException or InvalidOperationException) { return false; }
    }
    internal static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    // Message IDs and proof times can differ on a repeated observation; the proven effect cannot.
    internal static string ProofHash(CalendarEventResult r) => Hash(JsonSerializer.Serialize(new { r.Outcome, r.ProviderCode,
        r.CalendarId, r.ExternalCalendarId, r.Event, r.ObservedRevision }, Json));
    private static T Read<T>(string json, Func<JsonElement, T> read) => ClosedIntegrationJson.Read(json, MaxBytes, root =>
    { Duplicates(root); return read(root); });
    private static void Duplicates(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in e.EnumerateObject()) { if (!names.Add(p.Name)) throw new IntegrationContractException(); Duplicates(p.Value); }
        }
        else if (e.ValueKind == JsonValueKind.Array) foreach (var item in e.EnumerateArray()) Duplicates(item);
    }
    private static void Header(int version, Guid message, Guid operation, Guid correlation, string type, Guid delivery,
        string key, string hash, string provider, string calendar)
    {
        if (version != 1 || message == Guid.Empty || operation == Guid.Empty || correlation == Guid.Empty || delivery == Guid.Empty
            || !CalendarEventSnapshots.IsAction(type) || key != $"calendar-event-{delivery:D}"
            || hash is not { Length: 64 } || hash.Any(c => !char.IsAsciiHexDigitLower(c))
            || !Text(provider, 50) || !Text(calendar, 500)) throw new IntegrationContractException();
    }
    private static bool Text(string? value, int max) => value is { Length: > 0 } && value.Length <= max
        && value == value.Trim() && !value.Any(char.IsControl);
}
