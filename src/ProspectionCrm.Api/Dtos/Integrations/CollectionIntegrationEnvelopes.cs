using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ProspectionCrm.Api.Services;

namespace ProspectionCrm.Api.Dtos.Integrations;

public sealed record CollectionCommandPayload(Guid RequestId, string Query, int MaxItems);
public sealed record CollectionCandidate(string ExternalId, string Title, string? SourceUrl = null,
    string? Location = null, string? Description = null);
public sealed record CollectionResultPayload(Guid RequestId, string Outcome, IReadOnlyList<CollectionCandidate> Items);
public sealed record CollectionCommandEnvelope(int Version, Guid MessageId, Guid OperationId,
    Guid CorrelationId, string Type, CollectionCommandPayload Payload);
public sealed record CollectionResultEnvelope(int Version, Guid MessageId, Guid OperationId,
    Guid CorrelationId, Guid CommandMessageId, string Type, CollectionResultPayload Payload);

// Independent of technical.probe v1. Nothing supplied by the sender selects a CRM destination.
public static class CollectionIntegrationContracts
{
    public const string SourceType = "remote-collection";
    public const string Type = "collection.request";
    public const int Version = 1;
    public const int MaxCommandBytes = 4096;
    public const int MaxResultBytes = 512 * 1024;
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string Serialize(CollectionCommandEnvelope value)
    { var json = JsonSerializer.Serialize(value, Options); _ = ParseCommand(json); return json; }
    public static string Serialize(CollectionResultEnvelope value)
    { var json = JsonSerializer.Serialize(value, Options); _ = ParseResult(json); return json; }

    public static CollectionCommandEnvelope ParseCommand(string json) => Read(json, MaxCommandBytes, root =>
    {
        Shape(root, ["version", "messageId", "operationId", "correlationId", "type", "payload"]);
        Header(root);
        var p = root.GetProperty("payload");
        Shape(p, ["requestId", "query", "maxItems"]);
        return new CollectionCommandEnvelope(Version, Id(root, "messageId"), Id(root, "operationId"),
            Id(root, "correlationId"), Type, new(Id(p, "requestId"), Text(p, "query", 200, allowEmpty: true)!, Limit(p)));
    });

    public static CollectionResultEnvelope ParseResult(string json) => Read(json, MaxResultBytes, root =>
    {
        Shape(root, ["version", "messageId", "operationId", "correlationId", "commandMessageId", "type", "payload"]);
        Header(root);
        var p = root.GetProperty("payload");
        Shape(p, ["requestId", "outcome", "items"]);
        var outcome = p.GetProperty("outcome").GetString();
        var items = p.GetProperty("items");
        if (outcome is not ("succeeded" or "failed") || items.ValueKind != JsonValueKind.Array
            || items.GetArrayLength() > 100 || (outcome == "succeeded" ? items.GetArrayLength() == 0 : items.GetArrayLength() != 0))
            throw new IntegrationContractException();
        var result = new List<CollectionCandidate>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in items.EnumerateArray())
        {
            Shape(item, ["externalId", "title"], ["sourceUrl", "location", "description"]);
            var id = Text(item, "externalId", 500)!;
            if (!ids.Add(id.Trim())) throw new IntegrationContractException();
            var url = Text(item, "sourceUrl", 2048, optional: true);
            if (url is not null && IngestionNormalization.UrlKey(url) is null) throw new IntegrationContractException();
            result.Add(new(id, Text(item, "title", 200)!, url, Text(item, "location", 200, optional: true),
                Text(item, "description", 10000, optional: true)));
        }
        return new CollectionResultEnvelope(Version, Id(root, "messageId"), Id(root, "operationId"),
            Id(root, "correlationId"), Id(root, "commandMessageId"), Type, new(Id(p, "requestId"), outcome!, result));
    });

    // Only these bounded criteria are forwarded; never ConfigurationJson or a callback URL.
    public static CollectionCommandPayload Criteria(string? json, Guid requestId) => Read(json ?? "{}", MaxCommandBytes, root =>
    {
        Shape(root, [], ["query", "maxItems"]);
        return new CollectionCommandPayload(requestId, Text(root, "query", 200, optional: true, allowEmpty: true) ?? "",
            root.TryGetProperty("maxItems", out _) ? Limit(root) : 100);
    });

    // Routing/size selection only: authentication still signs the entire original byte sequence.
    public static bool IsCollection(ReadOnlyMemory<byte> body)
    {
        try
        {
            using var document = JsonDocument.Parse(body, new() { MaxDepth = 6 });
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object && root.EnumerateObject().Count(p => p.Name == "type") == 1
                && root.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String && type.GetString() == Type;
        }
        catch (JsonException) { return false; }
    }

    private static T Read<T>(string json, int max, Func<JsonElement, T> parse)
    {
        if (json is null || Encoding.UTF8.GetByteCount(json) > max) throw new IntegrationContractException();
        try
        {
            using var document = JsonDocument.Parse(json, new() { MaxDepth = 6 });
            return parse(document.RootElement);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException or KeyNotFoundException)
        { throw new IntegrationContractException(); }
    }
    private static void Shape(JsonElement value, string[] required, string[]? optional = null)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new IntegrationContractException();
        var actual = value.EnumerateObject().Select(p => p.Name).ToArray();
        var names = actual.ToHashSet(StringComparer.Ordinal);
        if (names.Count != actual.Length || !required.All(names.Contains)
            || actual.Any(n => !required.Contains(n, StringComparer.Ordinal) && !(optional ?? []).Contains(n, StringComparer.Ordinal)))
            throw new IntegrationContractException();
    }
    private static void Header(JsonElement root)
    {
        if (!root.GetProperty("version").TryGetInt32(out var v) || v != Version || root.GetProperty("type").GetString() != Type)
            throw new IntegrationContractException();
    }
    private static Guid Id(JsonElement value, string name)
    {
        if (!Guid.TryParseExact(value.GetProperty(name).GetString(), "D", out var id) || id == Guid.Empty)
            throw new IntegrationContractException();
        return id;
    }
    private static int Limit(JsonElement value)
    {
        if (!value.GetProperty("maxItems").TryGetInt32(out var count) || count is < 1 or > 100) throw new IntegrationContractException();
        return count;
    }
    private static string? Text(JsonElement value, string name, int max, bool optional = false, bool allowEmpty = false)
    {
        if (!value.TryGetProperty(name, out var p) && optional) return null;
        if (p.ValueKind != JsonValueKind.String) throw new IntegrationContractException();
        var text = p.GetString()!;
        if (text.Length > max || (!allowEmpty && string.IsNullOrWhiteSpace(text))) throw new IntegrationContractException();
        return text;
    }
}
