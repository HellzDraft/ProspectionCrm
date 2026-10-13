using System.Text;
using System.Text.Json;

namespace ProspectionCrm.Api.Dtos.Integrations;

public sealed record ProbeCommandPayload(Guid ProbeId);
public sealed record ProbeResultPayload(Guid ProbeId, string Outcome);
public sealed record IntegrationCommandEnvelope(int Version, Guid MessageId, Guid OperationId,
    Guid CorrelationId, string Type, ProbeCommandPayload Payload);
public sealed record IntegrationResultEnvelope(int Version, Guid MessageId, Guid OperationId,
    Guid CorrelationId, Guid CommandMessageId, string Type, ProbeResultPayload Payload);

// Diagnostics deliberately contain neither the supplied JSON nor parser exception details.
public sealed class IntegrationContractException() : Exception("invalid-integration-envelope");

public static class IntegrationContracts
{
    public const int Version = 1;
    public const string ProbeType = "technical.probe";
    public const int MaxEnvelopeBytes = 4096;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static string Serialize(IntegrationCommandEnvelope envelope)
    {
        var json = JsonSerializer.Serialize(envelope, JsonOptions);
        _ = ParseCommand(json);
        return json;
    }

    public static string Serialize(IntegrationResultEnvelope envelope)
    {
        var json = JsonSerializer.Serialize(envelope, JsonOptions);
        _ = ParseResult(json);
        return json;
    }

    public static IntegrationCommandEnvelope ParseCommand(string json) => Read(json, root =>
    {
        Shape(root, "version", "messageId", "operationId", "correlationId", "type", "payload");
        Header(root);
        var payload = root.GetProperty("payload");
        Shape(payload, "probeId");
        return new IntegrationCommandEnvelope(Version, Id(root, "messageId"), Id(root, "operationId"),
            Id(root, "correlationId"), ProbeType, new(Id(payload, "probeId")));
    });

    public static IntegrationResultEnvelope ParseResult(string json) => Read(json, root =>
    {
        Shape(root, "version", "messageId", "operationId", "correlationId", "commandMessageId", "type", "payload");
        Header(root);
        var payload = root.GetProperty("payload");
        Shape(payload, "probeId", "outcome");
        var outcome = payload.GetProperty("outcome").GetString();
        if (outcome is not ("succeeded" or "failed")) throw new IntegrationContractException();
        return new IntegrationResultEnvelope(Version, Id(root, "messageId"), Id(root, "operationId"),
            Id(root, "correlationId"), Id(root, "commandMessageId"), ProbeType, new(Id(payload, "probeId"), outcome));
    });

    private static T Read<T>(string json, Func<JsonElement, T> parse)
    {
        if (json is null || Encoding.UTF8.GetByteCount(json) > MaxEnvelopeBytes) throw new IntegrationContractException();
        try
        {
            using var document = JsonDocument.Parse(json, new() { MaxDepth = 4 });
            return parse(document.RootElement);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException)
        { throw new IntegrationContractException(); }
    }

    private static void Shape(JsonElement value, params string[] fields)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new IntegrationContractException();
        var actual = value.EnumerateObject().Select(x => x.Name).ToArray();
        // Count plus set equality rejects duplicates, omissions, unknown fields and alternate casing.
        if (actual.Length != fields.Length || !actual.ToHashSet(StringComparer.Ordinal).SetEquals(fields))
            throw new IntegrationContractException();
    }

    private static void Header(JsonElement value)
    {
        if (!value.GetProperty("version").TryGetInt32(out var version) || version != Version
            || value.GetProperty("type").GetString() != ProbeType) throw new IntegrationContractException();
    }

    private static Guid Id(JsonElement value, string name)
    {
        if (!Guid.TryParseExact(value.GetProperty(name).GetString(), "D", out var id) || id == Guid.Empty)
            throw new IntegrationContractException();
        return id;
    }
}
