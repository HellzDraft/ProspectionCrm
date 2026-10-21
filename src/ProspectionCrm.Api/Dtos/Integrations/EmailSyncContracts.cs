using static ProspectionCrm.Api.Dtos.Integrations.ClosedIntegrationJson;
using System.Net.Mail;
using System.Text;
using System.Text.Json;

namespace ProspectionCrm.Api.Dtos.Integrations;

public sealed record EmailSyncCommandPayload(Guid AccountRef, string? PreviousCursor, int MaxItems);
public sealed record InboundEmail(string ExternalMessageId, string? ExternalThreadId, string FromAddress,
    IReadOnlyList<string> ToAddresses, IReadOnlyList<string>? CcAddresses, string? Subject, string? BodyText, DateTimeOffset OccurredAt);
public sealed record EmailSyncResultPayload(Guid AccountRef, string Outcome, string? NextCursor, IReadOnlyList<InboundEmail> Messages);
public sealed record EmailSyncCommand(int Version, Guid MessageId, Guid OperationId, Guid CorrelationId, string Type, EmailSyncCommandPayload Payload);
public sealed record EmailSyncResult(int Version, Guid MessageId, Guid OperationId, Guid CorrelationId, Guid CommandMessageId, string Type, EmailSyncResultPayload Payload);

public static class EmailSyncContracts
{
    public const string Type = "email.sync";
    public const int Version = 1;
    public const int MaxCommandBytes = 16 * 1024;
    public const int MaxResultBytes = 512 * 1024;
    public const int MaxBodyLength = 32000;
    public const int MaxRecipients = 100; // Combined To + Cc, before deduplication.
    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static string Serialize(EmailSyncCommand value) => JsonSerializer.Serialize(ParseCommand(JsonSerializer.Serialize(value, Options)), Options);
    public static string Serialize(EmailSyncResult value)
    {
        var json = JsonSerializer.Serialize(ParseResult(JsonSerializer.Serialize(value, Options)), Options);
        if (Encoding.UTF8.GetByteCount(json) > MaxResultBytes) throw new IntegrationContractException();
        return json;
    }
    public static EmailSyncCommand ParseCommand(string json) => Read(json, MaxCommandBytes, root =>
    {
        Header(root, false);
        var p = root.GetProperty("payload"); Shape(p, ["accountRef", "previousCursor", "maxItems"]);
        if (!p.GetProperty("maxItems").TryGetInt32(out var max) || max is < 1 or > 100) throw new IntegrationContractException();
        return new EmailSyncCommand(Version, Id(root, "messageId"), Id(root, "operationId"), Id(root, "correlationId"), Type,
            new(Id(p, "accountRef"), Text(p, "previousCursor", 2048, true), max));
    });
    public static EmailSyncResult ParseResult(string json) => Read(json, MaxResultBytes, root =>
    {
        Header(root, true);
        var p = root.GetProperty("payload"); Shape(p, ["accountRef", "outcome", "nextCursor", "messages"]);
        var outcome = Text(p, "outcome", 9);
        var items = p.GetProperty("messages");
        if (outcome is not ("succeeded" or "failed") || items.ValueKind != JsonValueKind.Array || items.GetArrayLength() > 100
            || outcome == "failed" && items.GetArrayLength() != 0) throw new IntegrationContractException();
        var next = Text(p, "nextCursor", 2048, true);
        if (outcome == "failed" && next is not null) throw new IntegrationContractException();
        var messages = new List<InboundEmail>(); var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in items.EnumerateArray())
        {
            Shape(item, ["externalMessageId", "fromAddress", "toAddresses", "occurredAt"], ["externalThreadId", "ccAddresses", "subject", "bodyText"]);
            var id = Text(item, "externalMessageId", 500)!;
            if (!ids.Add(id)) throw new IntegrationContractException();
            var from = Text(item, "fromAddress", 320)!;
            if (!IsAddress(from)) throw new IntegrationContractException();
            var to = Addresses(item.GetProperty("toAddresses"));
            var cc = item.TryGetProperty("ccAddresses", out var c) && c.ValueKind != JsonValueKind.Null ? Addresses(c) : [];
            if (to.Length + cc.Length > MaxRecipients) throw new IntegrationContractException();
            var date = item.GetProperty("occurredAt");
            var dateText = date.GetString();
            if (dateText is null || !(dateText.EndsWith('Z') || dateText.Length >= 6 && dateText[^3] == ':' && dateText[^6] is '+' or '-')
                || !date.TryGetDateTimeOffset(out var at)) throw new IntegrationContractException();
            // PostgreSQL timestamps have microsecond precision; normalize before durable comparison.
            at = new DateTimeOffset(at.UtcTicks - at.UtcTicks % 10, TimeSpan.Zero);
            messages.Add(new(id, Text(item, "externalThreadId", 500, true), from,
                NormalizeAddresses(to), NormalizeAddresses(cc), Text(item, "subject", 1000, true, true),
                Text(item, "bodyText", MaxBodyLength, true, true), at));
        }
        // Provider ordering is not part of a message or batch identity.
        return new EmailSyncResult(Version, Id(root, "messageId"), Id(root, "operationId"), Id(root, "correlationId"), Id(root, "commandMessageId"), Type,
            new(Id(p, "accountRef"), outcome!, next, messages.OrderBy(x => x.ExternalMessageId, StringComparer.Ordinal).ToArray()));
    });

    public static bool IsEmailSync(ReadOnlyMemory<byte> body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body, new() { MaxDepth = 6 }); var root = doc.RootElement;
            return root.ValueKind == JsonValueKind.Object && root.EnumerateObject().Count(x => x.Name == "type") == 1
                && root.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String && type.GetString() == Type;
        }
        catch (JsonException) { return false; }
    }
    public static bool IsAddress(string? value) => value is { Length: > 0 and <= 320 } && !value.Any(x => char.IsWhiteSpace(x) || char.IsControl(x))
        && value.Contains('@') && MailAddress.TryCreate(value, out var address) && address.Address == value;
    public static string[] NormalizeAddresses(IEnumerable<string> addresses) => addresses.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    private static string[] Addresses(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > MaxRecipients) throw new IntegrationContractException();
        var result = value.EnumerateArray().Select(x => x.GetString()).ToArray();
        if (result.Any(x => !IsAddress(x))) throw new IntegrationContractException();
        return result.Select(x => x!).ToArray();
    }
    private static void Header(JsonElement root, bool result)
    {
        Shape(root, result ? ["version", "messageId", "operationId", "correlationId", "commandMessageId", "type", "payload"]
            : ["version", "messageId", "operationId", "correlationId", "type", "payload"]);
        if (!root.GetProperty("version").TryGetInt32(out var v) || v != Version || root.GetProperty("type").GetString() != Type)
            throw new IntegrationContractException();
    }
}
