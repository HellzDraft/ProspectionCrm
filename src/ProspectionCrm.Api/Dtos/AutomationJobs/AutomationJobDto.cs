using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using ProspectionCrm.Api.Entities;
using ProspectionCrm.Api.Services.Automation;

namespace ProspectionCrm.Api.Dtos.AutomationJobs;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class EnqueueAutomationJobRequest
{
    public Guid? AutomationRuleId { get; init; }
    [Required] public required string TriggerTypeCode { get; init; }
    [MaxLength(AutomationJobLimits.TriggerKeyLength)] public string? TriggerKey { get; init; }
    [Required] public required string ActionCategoryCode { get; init; }
    [Range(AutomationJobLimits.MinPriority, AutomationJobLimits.MaxPriority)] public int? Priority { get; init; }
    public DateTimeOffset? AvailableAt { get; init; }
    public string? ContextJson { get; init; }
}

// LeaseOwner is an internal capability, not an administration API field.
public sealed record AutomationJobDto(Guid Id, Guid WorkspaceId, Guid? AutomationRuleId, string TriggerTypeCode,
    string? TriggerKey, string ActionCategoryCode, string StatusCode, int Priority, DateTimeOffset AvailableAt,
    DateTimeOffset? LeaseExpiresAt, int AttemptCount, string? ContextJson, string? LastError,
    DateTimeOffset CreatedAt, DateTimeOffset? UpdatedAt, DateTimeOffset? CompletedAt)
{
    public static AutomationJobDto From(AutomationJob x) => new(x.Id, x.WorkspaceId, x.AutomationRuleId,
        x.TriggerTypeCode, x.TriggerKey, x.ActionCategoryCode, x.StatusCode, x.Priority, x.AvailableAt,
        x.LeaseExpiresAt, x.AttemptCount, x.ContextJson,
        x.LastError is null ? null : AutomationJobErrors.Sanitize(x.LastError), x.CreatedAt, x.UpdatedAt, x.CompletedAt);
}

public sealed record AutomationJobsPageDto(int Offset, int Limit, int Total, bool HasMore, IReadOnlyList<AutomationJobDto> Items);
