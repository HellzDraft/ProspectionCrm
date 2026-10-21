using ProspectionCrm.Api.Dtos.CalendarEvents;
using System.Text.Json.Serialization;
using ProspectionCrm.Api.Services.Automation;
using ProspectionCrm.Api.Dtos.EmailMessages;

namespace ProspectionCrm.Api.Dtos.AutomationActionRequests;

public sealed record AutomationActionRequestDto(Guid Id, Guid? AutomationJobId, Guid? AutomationRuleId,
    string? RuleName, string DecisionRequirementCode, string StatusCode, string ActionTypeCode,
    string ActionCategoryCode, CreateCrmTaskPlan? ActionPlan, string RequestedReasonCode,
    DateTimeOffset RequestedAt, DateTimeOffset? UpdatedAt, DateTimeOffset? DecidedAt, Guid? DecidedByUserId,
    string? DecisionNote, string? JobStatusCode, bool IsStale, EmailSendSnapshot? EmailPlan = null, string? ContentFingerprint = null, CalendarEventSnapshot? CalendarPlan = null);

public sealed record AutomationActionRequestsPageDto(int Offset, int Limit, int TotalCount, bool HasMore,
    IReadOnlyList<AutomationActionRequestDto> Items);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class DecideAutomationActionRequest
{
    public string? DecisionNote { get; set; }
}
