using System.Text;
using System.Text.Json;
using static ProspectionCrm.Api.Dtos.Integrations.ClosedIntegrationJson;

namespace ProspectionCrm.Api.Dtos.Integrations;

public sealed record CalendarSyncCommandPayload(Guid CalendarRef, Guid JobRef, string? PreviousCursor, int MaxItems);
public sealed record InboundCalendarEvent(string ExternalEventId, long Revision, string Title, string? Description,
    string? Location, DateTimeOffset StartsAt, DateTimeOffset EndsAt, bool IsAllDay, string? TimeZoneId,
    string Status, string? SeriesId = null, string? OccurrenceId = null);
public sealed record CalendarSyncResultPayload(Guid CalendarRef, Guid JobRef, string Outcome, string? NextCursor, IReadOnlyList<InboundCalendarEvent> Events);
public sealed record CalendarSyncCommand(int Version, Guid MessageId, Guid OperationId, Guid CorrelationId, string Type, CalendarSyncCommandPayload Payload);
public sealed record CalendarSyncResult(int Version, Guid MessageId, Guid OperationId, Guid CorrelationId, Guid CommandMessageId, string Type, CalendarSyncResultPayload Payload);

public static class CalendarSyncContracts
{
    public const string Type = "calendar.sync";
    public const int Version = 1;
    public const int MaxResultBytes = 512 * 1024;
    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    public static string Serialize(CalendarSyncCommand value) => JsonSerializer.Serialize(ParseCommand(JsonSerializer.Serialize(value, Options)), Options);
    public static string Serialize(CalendarSyncResult value)
    {
        var json = JsonSerializer.Serialize(ParseResult(JsonSerializer.Serialize(value, Options)), Options);
        if (Encoding.UTF8.GetByteCount(json) > MaxResultBytes) throw new IntegrationContractException();
        return json;
    }
    public static CalendarSyncCommand ParseCommand(string json) => Read(json, 16 * 1024, root =>
    {
        Header(root, false);
        var p = root.GetProperty("payload"); Shape(p, ["calendarRef", "jobRef", "previousCursor", "maxItems"]);
        if (!p.GetProperty("maxItems").TryGetInt32(out var max) || max is < 1 or > 100) throw new IntegrationContractException();
        return new CalendarSyncCommand(Version, Id(root, "messageId"), Id(root, "operationId"), Id(root, "correlationId"), Type,
            new(Id(p, "calendarRef"), Id(p, "jobRef"), Text(p, "previousCursor", 2048, true), max));
    });
    public static CalendarSyncResult ParseResult(string json) => Read(json, MaxResultBytes, root =>
    {
        Header(root, true);
        var p = root.GetProperty("payload"); Shape(p, ["calendarRef", "jobRef", "outcome", "nextCursor", "events"]);
        var outcome = Text(p, "outcome", 9); var next = Text(p, "nextCursor", 2048, true);
        var items = p.GetProperty("events");
        if (outcome is not ("succeeded" or "failed") || items.ValueKind != JsonValueKind.Array || items.GetArrayLength() > 100
            || outcome == "failed" && (items.GetArrayLength() != 0 || next is not null)) throw new IntegrationContractException();
        var events = new List<InboundCalendarEvent>(); var ids = new HashSet<string>(StringComparer.Ordinal);
        var occurrences = new HashSet<(string, string)>();
        foreach (var item in items.EnumerateArray())
        {
            Shape(item, ["externalEventId", "revision", "title", "startsAt", "endsAt", "isAllDay", "status"],
                ["description", "location", "timeZoneId", "seriesId", "occurrenceId"]);
            var id = Text(item, "externalEventId", 500)!;
            if (!ids.Add(id) || !item.GetProperty("revision").TryGetInt64(out var revision) || revision < 0) throw new IntegrationContractException();
            var start = Date(item, "startsAt"); var end = Date(item, "endsAt");
            if (Utc(end) <= Utc(start)) throw new IntegrationContractException();
            var zoneId = Text(item, "timeZoneId", 100, true); var zone = Zone(zoneId);
            // UTC is an absolute instant; nonzero local offsets must agree with the supplied IANA zone.
            if (zone is not null && (start.Offset != TimeSpan.Zero && zone.GetUtcOffset(start) != start.Offset || end.Offset != TimeSpan.Zero && zone.GetUtcOffset(end) != end.Offset)) throw new IntegrationContractException();
            var allDay = item.GetProperty("isAllDay").GetBoolean();
            var localStart = zone is null ? start : TimeZoneInfo.ConvertTime(start, zone);
            var localEnd = zone is null ? end : TimeZoneInfo.ConvertTime(end, zone);
            if (allDay && (zone is null || localStart.TimeOfDay != TimeSpan.Zero || localEnd.TimeOfDay != TimeSpan.Zero
                || localEnd.Date <= localStart.Date)) throw new IntegrationContractException();
            var status = Text(item, "status", 24);
            if (status is not ("confirmed" or "cancelled")) throw new IntegrationContractException();
            var series = Text(item, "seriesId", 500, true); var occurrence = Text(item, "occurrenceId", 500, true);
            if ((series is null) != (occurrence is null) || series is not null && !occurrences.Add((series, occurrence!))) throw new IntegrationContractException();
            events.Add(new(id, revision, Text(item, "title", 500)!, Text(item, "description", 10000, true, true),
                Text(item, "location", 500, true, true), Utc(start), Utc(end), allDay, zoneId, status!, series, occurrence));
        }
        return new CalendarSyncResult(Version, Id(root, "messageId"), Id(root, "operationId"), Id(root, "correlationId"), Id(root, "commandMessageId"), Type,
            new(Id(p, "calendarRef"), Id(p, "jobRef"), outcome!, next, events.OrderBy(x => x.ExternalEventId, StringComparer.Ordinal).ToArray()));
    });
    public static TimeZoneInfo? Zone(string? id)
    {
        if (id is null) return null;
        try
        {
            if (id.Length > 100 || !(id == "UTC" || TimeZoneInfo.TryConvertIanaIdToWindowsId(id, out _))) throw new IntegrationContractException();
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException) { throw new IntegrationContractException(); }
    }
    private static DateTimeOffset Date(JsonElement item, string name)
    {
        var p = item.GetProperty(name); var s = p.GetString();
        if (s is null || !(s.EndsWith('Z') || s.Length >= 6 && s[^3] == ':' && s[^6] is '+' or '-') || !p.TryGetDateTimeOffset(out var at))
            throw new IntegrationContractException();
        return at;
    }
    private static DateTimeOffset Utc(DateTimeOffset at) => new(at.UtcTicks - at.UtcTicks % 10, TimeSpan.Zero);
    private static void Header(JsonElement root, bool result)
    {
        Shape(root, result ? ["version", "messageId", "operationId", "correlationId", "commandMessageId", "type", "payload"]
            : ["version", "messageId", "operationId", "correlationId", "type", "payload"]);
        if (!root.GetProperty("version").TryGetInt32(out var v) || v != Version || root.GetProperty("type").GetString() != Type) throw new IntegrationContractException();
    }
    public static bool IsCalendarSync(ReadOnlyMemory<byte> body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body, new() { MaxDepth = 6 }); var root = doc.RootElement;
            return root.ValueKind == JsonValueKind.Object && root.EnumerateObject().Count(x => x.Name == "type") == 1
                && root.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String && type.GetString() == Type;
        }
        catch (JsonException) { return false; }
    }
}
