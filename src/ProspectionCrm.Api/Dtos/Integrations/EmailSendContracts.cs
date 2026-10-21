using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ProspectionCrm.Api.Dtos.EmailMessages;
using ProspectionCrm.Api.Services.Email;

namespace ProspectionCrm.Api.Dtos.Integrations;

public sealed record EmailSendCommand(int Version, Guid MessageId, Guid OperationId, Guid CorrelationId,
    string Type, Guid DeliveryId, string IdempotencyKey, EmailSendSnapshot Snapshot);
public sealed record EmailSendResult(int Version, Guid MessageId, Guid OperationId, Guid CorrelationId,
    Guid CommandMessageId, string Type, Guid DeliveryId, string IdempotencyKey, string Outcome,
    string? ProviderCode, string? ExternalMessageId, DateTimeOffset? AcceptedAt);

public static class EmailSendContracts
{
    public const string Type = "email.send";
    public const int ResultBytes = 4096;
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    { PropertyNameCaseInsensitive = false, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 6,
        RespectRequiredConstructorParameters = true };
    public static string Serialize(EmailSendCommand value) { Validate(value); return JsonSerializer.Serialize(value, Options); }
    public static string Serialize(EmailSendResult value) { Validate(value); return JsonSerializer.Serialize(value, Options); }
    public static EmailSendCommand ParseCommand(string json)
    { var value = Read<EmailSendCommand>(json, 65536); Validate(value); return value; }
    public static EmailSendResult ParseResult(string json)
    { var value = Read<EmailSendResult>(json, ResultBytes); Validate(value); return value; }
    public static bool IsEmailSend(string json)
    {
        try { using var doc = JsonDocument.Parse(json, new() { MaxDepth = 6 }); return doc.RootElement.TryGetProperty("type", out var p) && p.GetString() == Type; }
        catch (Exception e) when (e is JsonException or InvalidOperationException) { return false; }
    }
    private static T Read<T>(string json, int max)
    {
        if (json is null || Encoding.UTF8.GetByteCount(json) > max) throw new IntegrationContractException();
        try
        {
            using var doc = JsonDocument.Parse(json, new() { MaxDepth = 6 }); CheckDuplicates(doc.RootElement);
            return JsonSerializer.Deserialize<T>(json, Options) ?? throw new IntegrationContractException();
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or ArgumentException) { throw new IntegrationContractException(); }
    }
    private static void CheckDuplicates(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in e.EnumerateObject()) { if (!names.Add(p.Name)) throw new IntegrationContractException(); CheckDuplicates(p.Value); }
        }
        if (e.ValueKind == JsonValueKind.Array) foreach (var child in e.EnumerateArray()) CheckDuplicates(child);
    }
    private static void Header(int version, Guid message, Guid operation, Guid correlation, string type, Guid delivery, string key)
    {
        if (version != 1 || message == Guid.Empty || operation == Guid.Empty || correlation == Guid.Empty
            || type != Type || delivery == Guid.Empty || key != $"email-send-{delivery:D}") throw new IntegrationContractException();
    }
    private static void Validate(EmailSendCommand v)
    {
        Header(v.Version, v.MessageId, v.OperationId, v.CorrelationId, v.Type, v.DeliveryId, v.IdempotencyKey);
        if (v.Snapshot is null) throw new IntegrationContractException();
        try { _ = EmailSendSnapshots.Serialize(v.Snapshot); } catch (Exception e) when (e is EmailSyncException or ArgumentException) { throw new IntegrationContractException(); }
    }
    private static void Validate(EmailSendResult v)
    {
        Header(v.Version, v.MessageId, v.OperationId, v.CorrelationId, v.Type, v.DeliveryId, v.IdempotencyKey);
        if (v.CommandMessageId == Guid.Empty || v.Outcome is not ("accepted" or "not-sent" or "unknown" or "failed")) throw new IntegrationContractException();
        if (v.Outcome == "accepted")
        {
            if (!Text(v.ProviderCode, 50) || !Text(v.ExternalMessageId, 500) || v.AcceptedAt is null
                || v.AcceptedAt.Value.Offset != TimeSpan.Zero) throw new IntegrationContractException();
        }
        else if (v.ProviderCode is not null || v.ExternalMessageId is not null || v.AcceptedAt is not null) throw new IntegrationContractException();
    }
    private static bool Text(string? s, int max) => s is { Length: > 0 } && s.Length <= max && s == s.Trim() && !s.Any(char.IsControl);
}
