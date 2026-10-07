using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using ProspectionCrm.Api.Services.Automation;

namespace ProspectionCrm.Api.Dtos.AutomationSettings;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class UpdateAutomationSettingsRequest
{
    public required bool IsEnabled { get; init; }
    [Required] public required string OperatingModeCode { get; init; }
    [Range(1, 100)] public required int MaxExecutionsPerMinute { get; init; }
    [Range(1, 10000)] public required int MaxExecutionsPerDay { get; init; }
    [Range(1, 20)] public required int MaxConsecutiveFailures { get; init; }
}

public sealed record AutomationSettingsDto(Guid WorkspaceId, bool IsEnabled, string OperatingModeCode,
    int MaxExecutionsPerMinute, int MaxExecutionsPerDay, int MaxConsecutiveFailures,
    DateTimeOffset CreatedAt, DateTimeOffset? UpdatedAt, IReadOnlyList<AutomationSafetyDecision> Decisions);
