namespace ProspectionCrm.Api.Services.Automation;

public static class AutomationJobStatuses
{
    public const string Pending = "pending", Leased = "leased", Completed = "completed",
        Failed = "failed", Cancelled = "cancelled";
    public static bool IsValid(string? code) => code is Pending or Leased or Completed or Failed or Cancelled;
}

public static class AutomationJobTriggers
{
    public const string Manual = "manual";
    public static bool IsValid(string? code) => code is Manual;
}

public static class AutomationJobLimits
{
    public const int DefaultPriority = 50, MinPriority = 0, MaxPriority = 100,
        TriggerKeyLength = 200, ContextBytes = 16384, StoredContextBytes = 65536,
        LastErrorLength = 2000, RecoveryBatchSize = 100;
}

// Only controlled codes are persisted. Arbitrary exception messages are never public errors.
public static class AutomationJobErrors
{
    public const string JobFailed = "AutomationJobFailed", ProcessingRejected = "AutomationProcessingRejected";
    public static string Sanitize(string? error) => error is ProcessingRejected ? ProcessingRejected : JobFailed;
}

// Configured in code through IOptions; deliberately not part of AutomationRuntimeSettings.
public sealed class AutomationJobQueueOptions
{
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromMinutes(5);
}
