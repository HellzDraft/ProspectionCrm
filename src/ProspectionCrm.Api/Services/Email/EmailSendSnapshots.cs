using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ProspectionCrm.Api.Dtos.EmailMessages;
using ProspectionCrm.Api.Dtos.Integrations;
using ProspectionCrm.Api.Entities;
using ProspectionCrm.Api.Services.Automation;

namespace ProspectionCrm.Api.Services.Email;

public static class EmailSendSnapshots
{
    public const string ActionType = "email.send";
    public const int MaxBytes = 60000;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
        { PropertyNameCaseInsensitive = false, MaxDepth = 6 };

    public static EmailSendContent Normalize(EmailSendContent value)
    {
        if (value.EmailAccountId == Guid.Empty || value.ToAddresses is not { Length: > 0 }
            || value.ToAddresses.Length + (value.CcAddresses?.Length ?? 0) > EmailSyncContracts.MaxRecipients
            || value.ToAddresses.Concat(value.CcAddresses ?? []).Any(x => !EmailSyncContracts.IsAddress(x))
            || value.Subject is { } subject && (subject.Length > 1000 || subject.Any(char.IsControl) || !AutomationJson.SafeText(subject))
            || value.BodyText is null || value.BodyText.Length > EmailSyncContracts.MaxBodyLength
            || !AutomationJson.SafeText(value.BodyText) || value.BodyText.Any(x => char.IsControl(x) && x is not ('\r' or '\n' or '\t'))
            || Regex.IsMatch(value.BodyText, @"<\s*/?\s*[a-zA-Z][^>]*>|<!--|<!DOCTYPE", RegexOptions.IgnoreCase)
            || value.OpportunityId == Guid.Empty || value.CompanyId == Guid.Empty || value.ContactId == Guid.Empty)
            throw new EmailSyncException("EmailSendContentInvalid", 400);
        var to = EmailSyncContracts.NormalizeAddresses(value.ToAddresses);
        var normalized = value with { ToAddresses = to,
            CcAddresses = EmailSyncContracts.NormalizeAddresses((value.CcAddresses ?? []).Except(to, StringComparer.Ordinal)) };
        if (Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(normalized, Json)) > MaxBytes - 1000)
            throw new EmailSyncException("EmailSendContentTooLarge", 400);
        return normalized;
    }
    public static EmailSendContent? ParseConfiguration(string? json)
    {
        try
        {
            if (!AutomationJson.TryObject(json, AutomationDefinitionLimits.RuleJsonBytes, out _)) return null;
            var value = JsonSerializer.Deserialize<EmailSendContent>(json!, Json);
            return value is null ? null : Normalize(value);
        }
        catch (Exception e) when (e is JsonException or EmailSyncException or ArgumentException) { return null; }
    }
    public static EmailSendSnapshot From(EmailSendPlan plan) => new(1, plan.Id, plan.WorkspaceId, plan.EmailAccountId,
        plan.ConnectionId, plan.SubmittedRevision ?? plan.Revision, plan.FromAddressSnapshot,
        JsonSerializer.Deserialize<string[]>(plan.ToAddressesJson)!, JsonSerializer.Deserialize<string[]>(plan.CcAddressesJson)!,
        plan.Subject, plan.BodyText, plan.OpportunityId, plan.CompanyId, plan.ContactId);

    public static string Serialize(EmailSendSnapshot value)
    {
        var normalized = Normalize(new(value.EmailAccountId, value.ToAddresses, value.CcAddresses, value.Subject,
            value.BodyText, value.OpportunityId, value.CompanyId, value.ContactId));
        if (value.Version != 1 || value.PlanId == Guid.Empty || value.WorkspaceId == Guid.Empty
            || value.ConnectionId == Guid.Empty || value.Revision < 0 || !EmailSyncContracts.IsAddress(value.FromAddress)
            || value.CcAddresses is null || !value.ToAddresses.SequenceEqual(normalized.ToAddresses)
            || !value.CcAddresses.SequenceEqual(normalized.CcAddresses!)) throw new EmailSyncException("EmailSendSnapshotInvalid");
        var json = JsonSerializer.Serialize(value, Json);
        if (Encoding.UTF8.GetByteCount(json) > MaxBytes) throw new EmailSyncException("EmailSendContentTooLarge", 400);
        return json;
    }
    public static EmailSendSnapshot? Parse(string? json)
    {
        try
        {
            if (!AutomationJson.TryObject(json, ActionRequestCodes.PlanBytes, out var root)
                || root.EnumerateObject().Count() != 14) return null;
            var snapshot = JsonSerializer.Deserialize<EmailSendSnapshot>(json!, Json);
            if (snapshot is null) return null;
            _ = Serialize(snapshot); return snapshot;
        }
        catch (Exception e) when (e is JsonException or EmailSyncException or ArgumentException or InvalidOperationException) { return null; }
    }
    public static string Hash(EmailSendSnapshot value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Serialize(value))));
    public static bool ValidRequest(AutomationActionRequest request)
    {
        var snapshot = Parse(request.ActionPlanJson);
        return request.ActionTypeCode == ActionType && request.ActionCategoryCode == AutomationCategories.Email
            && request.DecisionRequirementCode is AutomationDecisions.Manual or AutomationDecisions.ApprovalRequired
            && snapshot is not null && snapshot.WorkspaceId == request.WorkspaceId && Hash(snapshot) == request.ContentFingerprint;
    }
}
