namespace ProspectionCrm.Api.Dtos.AutomationSupervision;

public sealed record AutomationWorkerConfigurationDto(bool ConfiguredEnabled, int IdleDelaySeconds,
    int RecoveryIntervalSeconds, int MaxAttempts);
public sealed record AutomationRuntimeSettingsSummary(bool IsEnabled, string OperatingModeCode,
    int MaxExecutionsPerMinute, int MaxExecutionsPerDay, int MaxConsecutiveFailures);
public sealed record AutomationQueueSummary(long PendingAvailableCount, long PendingScheduledCount,
    long LeasedActiveCount, long LeasedExpiredCount, long AwaitingApprovalCount, long FailedCount,
    long CancelledCount, long CompletedLast24HoursCount, DateTimeOffset? OldestOpenJobAt,
    DateTimeOffset? OldestPendingAvailableAt, DateTimeOffset? NextAvailableAt, DateTimeOffset? LastJobUpdatedAt);
public sealed record AutomationRequestSummary(long PendingCount, long PendingStaleCount,
    long ApprovedLast24HoursCount, long RejectedLast24HoursCount, DateTimeOffset? OldestPendingAt,
    DateTimeOffset? LastDecisionAt);
public sealed record AutomationExecutionSummary(long RunningCount, long AbandonedRunningCount,
    long SucceededLast24HoursCount, long FailedLast24HoursCount, long SkippedLast24HoursCount,
    long AutomaticEffectsLast24HoursCount, long HumanEffectsLast24HoursCount,
    long RecentTechnicalFailuresCount, DateTimeOffset? LastExecutionAt, DateTimeOffset? LastEffectAt);
public sealed record AutomationQuotaSummary(long Used, int Limit, bool IsReached, DateTimeOffset? AvailableAgainAt);
public sealed record AutomationQuotasSummary(AutomationQuotaSummary Minute, AutomationQuotaSummary Day);
public sealed record AutomationRuntimeGuardSnapshot(AutomationCircuitStatus Circuit, AutomationQuotasSummary Quotas);
public sealed record AutomationAlertDto(string Code, string SeverityCode, long Count, DateTimeOffset? Since,
    string? RelatedStateCode = null);
public sealed record AutomationSupervisionDto(Guid WorkspaceId, DateTimeOffset GeneratedAt,
    AutomationWorkerConfigurationDto Worker, AutomationRuntimeSettingsSummary RuntimeSettings,
    AutomationQueueSummary Queue, AutomationRequestSummary ActionRequests, AutomationExecutionSummary Executions,
    AutomationQuotasSummary Quotas, AutomationCircuitStatus Circuit, IReadOnlyList<AutomationAlertDto> Alerts);
