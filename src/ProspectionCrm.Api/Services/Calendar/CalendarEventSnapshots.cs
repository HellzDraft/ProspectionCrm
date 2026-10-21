using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ProspectionCrm.Api.Dtos.CalendarEvents;
using ProspectionCrm.Api.Dtos.Integrations;
using ProspectionCrm.Api.Entities;
using ProspectionCrm.Api.Services.Automation;

namespace ProspectionCrm.Api.Services.Calendar;

public static class CalendarEventSnapshots
{
    public const string CreateAction = "calendar.event.create", UpdateAction = "calendar.event.update";
    public static bool IsAction(string? code) => code is CreateAction or UpdateAction;
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
        { PropertyNameCaseInsensitive = false, MaxDepth = 6 };
    public static CalendarEventContent Normalize(CalendarEventContent value)
    {
        if (value is null || value.CalendarId == Guid.Empty || value.OperationCode is not ("create" or "update")
            || (value.OperationCode == "create") != (value.TargetCalendarEventId is null)
            || value.TargetCalendarEventId == Guid.Empty || value.OpportunityId == Guid.Empty
            || value.CompanyId == Guid.Empty || value.ContactId == Guid.Empty)
            throw new CalendarSyncException("CalendarPlanContentInvalid", 400);
        // Preserve the raw strings until the inbound contract has checked explicit offsets and IANA/DST rules.
        var id = value.CalendarId;
        var json = JsonSerializer.Serialize(new { version = 1, messageId = id, operationId = id, correlationId = id,
            commandMessageId = id, type = CalendarSyncContracts.Type, payload = new { calendarRef = id, jobRef = id,
            outcome = "succeeded", nextCursor = (string?)null, events = new[] { new { externalEventId = "plan", revision = 0,
                value.Title, value.Description, value.Location, value.StartsAt, value.EndsAt, value.IsAllDay, value.TimeZoneId,
                status = "confirmed" } } } }, Json);
        try
        {
            var e = CalendarSyncContracts.ParseResult(json).Payload.Events.Single();
            return value with { StartsAt = e.StartsAt.ToString("O"), EndsAt = e.EndsAt.ToString("O") };
        }
        catch (IntegrationContractException) { throw new CalendarSyncException("CalendarPlanContentInvalid", 400); }
    }
    public static CalendarEventSnapshot From(CalendarEventPlan p) => new(1, p.Id, p.WorkspaceId,
        p.CalendarAccountId, p.ConnectionId, p.SubmittedRevision ?? p.Revision,
        JsonSerializer.Deserialize<CalendarEventContent>(p.ContentJson, Json)!,
        p.TargetSnapshotJson is null ? null : JsonSerializer.Deserialize<CalendarEventTarget>(p.TargetSnapshotJson, Json));
    public static string Serialize(CalendarEventSnapshot value)
    {
        if (value.Version != 1 || value.PlanId == Guid.Empty || value.WorkspaceId == Guid.Empty
            || value.CalendarAccountId == Guid.Empty || value.ConnectionId == Guid.Empty || value.Revision < 0
            || Normalize(value.Content) != value.Content
            || (value.Content.OperationCode == "create") != (value.Target is null)
            || value.Target is { } t && (t.Id != value.Content.TargetCalendarEventId || t.Revision < 0
                || string.IsNullOrWhiteSpace(t.ExternalEventId) || t.StatusCode != "confirmed"
                || t.ContentHash.Length != 64 || (t.SeriesId is null) != (t.OccurrenceId is null)))
            throw new CalendarSyncException("CalendarPlanSnapshotInvalid");
        var json = JsonSerializer.Serialize(value, Json);
        if (Encoding.UTF8.GetByteCount(json) > 60000) throw new CalendarSyncException("CalendarPlanContentTooLarge", 400);
        return json;
    }
    public static CalendarEventSnapshot? Parse(string? json)
    {
        try
        {
            if (!AutomationJson.TryObject(json, ActionRequestCodes.PlanBytes, out var root)
                || root.EnumerateObject().Count() != 8) return null;
            var s = JsonSerializer.Deserialize<CalendarEventSnapshot>(json!, Json);
            if (s is null) return null;
            _ = Serialize(s); return s;
        }
        catch (Exception e) when (e is JsonException or CalendarSyncException or ArgumentException or InvalidOperationException or NullReferenceException) { return null; }
    }
    public static string Hash(CalendarEventSnapshot value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Serialize(value))));
    public static bool ValidRequest(AutomationActionRequest r)
    {
        var s = Parse(r.ActionPlanJson);
        return s is not null && r.ActionTypeCode == (s.Content.OperationCode == "create" ? CreateAction : UpdateAction)
            && r.ActionCategoryCode == AutomationCategories.Calendar && r.WorkspaceId == s.WorkspaceId
            && r.AutomationJobId is null && r.AutomationRuleId is null && ActionRequestCodes.RequiresDecision(r.DecisionRequirementCode)
            && r.ContentFingerprint == Hash(s) && r.RuleFingerprint == r.ContentFingerprint;
    }
    internal static CalendarEventTarget Target(CalendarEvent e) => new(e.Id, e.ProviderCode, e.ExternalEventId!, e.ExternalRevision!.Value,
        e.ExternalContentSha256!, e.Title, e.Description, e.Location, e.StartsAt, e.EndsAt, e.IsAllDay, e.TimeZoneId,
        e.StatusCode!, e.SeriesId, e.OccurrenceId, e.OpportunityId, e.ContactId);
}
