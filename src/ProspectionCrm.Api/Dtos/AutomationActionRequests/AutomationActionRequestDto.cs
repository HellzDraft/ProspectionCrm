using System.Text.Json.Serialization;
using ProspectionCrm.Api.Services.Automation;

namespace ProspectionCrm.Api.Dtos.AutomationActionRequests;

public sealed record AutomationActionRequestDto(Guid Id, Guid AutomationJobId, Guid AutomationRuleId,
    string? RuleName, string DecisionRequirementCode, string StatusCode, string ActionTypeCode,
    string ActionCategoryCode, CreateCrmTaskPlan? ActionPlan, string RequestedReasonCode,
    DateTimeOffset RequestedAt, DateTimeOffset? UpdatedAt, DateTimeOffset? DecidedAt, Guid? DecidedByUserId,
    string? DecisionNote, string? JobStatusCode, bool IsStale);

public sealed record AutomationActionRequestsPageDto(int Offset, int Limit, int TotalCount, bool HasMore,
    IReadOnlyList<AutomationActionRequestDto> Items);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class DecideAutomationActionRequest
{
    public string? DecisionNote { get; set; }
}
