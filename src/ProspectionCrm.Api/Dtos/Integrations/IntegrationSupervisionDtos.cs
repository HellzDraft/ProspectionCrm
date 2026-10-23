namespace ProspectionCrm.Api.Dtos.Integrations;

// Deliberately excludes payloads, external identities, hashes and persisted free-text diagnostics.
public sealed record IntegrationOperationView
{
    public Guid Id { get; init; }
    public Guid? ConnectionId { get; init; }
    public Guid CommandMessageId { get; init; }
    public Guid CorrelationId { get; init; }
    public string Type { get; init; } = "";
    public string StatusCode { get; init; } = "";
    public string State { get; init; } = "";
    public Guid? BusinessId { get; init; }
    public string? BusinessStatusCode { get; init; }
    public bool HasRecordedDiagnostic { get; init; }
    public string? DiagnosticCode { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public Guid? LatestAttemptId { get; init; }
    public int? LatestAttemptNumber { get; init; }
    public string? LatestAttemptStatusCode { get; init; }
    public DateTimeOffset? LatestAttemptStartedAt { get; init; }
    public long AttemptCount { get; init; }
    public long ActiveAttemptCount { get; init; }
    public long OldActiveAttemptCount { get; init; }
    public DateTimeOffset? OldestActiveAttemptAt { get; init; }
    public long UnprocessedReceiptCount { get; init; }
    public long OldUnprocessedReceiptCount { get; init; }
    public DateTimeOffset? OldestUnprocessedReceiptAt { get; init; }
    public DateTimeOffset? WaitingSince { get; init; }
    public DateTimeOffset? UncertainSince { get; init; }
}

public sealed record IntegrationSupervisionGroup(string Type, string StatusCode, string State, long Count,
    long ActiveAttemptCount, long OldActiveAttemptCount, long UnprocessedReceiptCount, long OldUnprocessedReceiptCount,
    DateTimeOffset? OldestActiveAttemptAt, DateTimeOffset? OldestUnprocessedReceiptAt, DateTimeOffset? OldestWaitingAt,
    DateTimeOffset? OldestUncertainAt);
public sealed record IntegrationSupervisionAlert(string Level, string Code, long Count, DateTimeOffset? OldestAt);
public sealed record IntegrationSupervisionSnapshot(Guid WorkspaceId, DateTimeOffset ObservedAt,
    string N8nHealth, int OldAttemptMinutes, int OldWaitingMinutes, long OperationCount,
    IReadOnlyList<IntegrationSupervisionGroup> Groups, IReadOnlyList<IntegrationSupervisionAlert> Alerts);
public sealed record IntegrationSupervisionPage(Guid WorkspaceId, DateTimeOffset ObservedAt, int Offset,
    int Limit, long Total, IReadOnlyList<IntegrationOperationView> Items);
