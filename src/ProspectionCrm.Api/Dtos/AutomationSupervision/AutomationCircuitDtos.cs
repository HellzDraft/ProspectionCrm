using System.Text.Json.Serialization;

namespace ProspectionCrm.Api.Dtos.AutomationSupervision;

public sealed record AutomationCircuitStatus(Guid WorkspaceId, int MaxConsecutiveFailures,
    long ConsecutiveFailureCount, long? LastAutomaticOutcomeSequence, DateTimeOffset? LastAutomaticOutcomeAt,
    DateTimeOffset? LastAutomaticSuccessAt, DateTimeOffset? LastAutomaticFailureAt, DateTimeOffset? OpenedAt,
    Guid? LastResetId, DateTimeOffset? LastResetAt, Guid? LastResetByUserId, string? LastResetNote,
    long ResetAfterOutcomeSequence)
{
    public string StatusCode => CanReset ? "open" : "closed";
    public bool CanReset => ConsecutiveFailureCount >= MaxConsecutiveFailures;
}

public sealed record AutomationCircuitResetDto(Guid Id, long ResetAfterOutcomeSequence,
    DateTimeOffset RequestedAt, Guid RequestedByUserId, string? Note);
public sealed record AutomationCircuitResetResult(AutomationCircuitStatus Status, bool WasReset,
    AutomationCircuitResetDto? Reset);
public sealed record AutomationCircuitResetsPage(int Offset, int Limit, int TotalCount, bool HasMore,
    IReadOnlyList<AutomationCircuitResetDto> Items);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class ResetAutomationCircuitRequest
{
    public string? Note { get; init; }
}
