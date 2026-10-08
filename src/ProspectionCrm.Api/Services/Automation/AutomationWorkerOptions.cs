namespace ProspectionCrm.Api.Services.Automation;

public sealed class AutomationWorkerOptions
{
    public const string SectionName = "AutomationWorker";
    public bool Enabled { get; set; }
    public int IdleDelaySeconds { get; set; } = 5;
    public int ErrorDelaySeconds { get; set; } = 10;
    public int RecoveryIntervalSeconds { get; set; } = 30;
    public int DeferredDelaySeconds { get; set; } = 60;
    public int WorkspaceBatchSize { get; set; } = 50;
    public int MaxAttempts { get; set; } = 3;
    public int InitialRetryDelaySeconds { get; set; } = 30;
    public int MaxRetryDelaySeconds { get; set; } = 120;
    public bool IsValid() => IdleDelaySeconds is >= 1 and <= 300
        && ErrorDelaySeconds is >= 1 and <= 300 && RecoveryIntervalSeconds is >= 1 and <= 300
        && DeferredDelaySeconds is >= 1 and <= 86400 && WorkspaceBatchSize is >= 1 and <= 100
        && MaxAttempts is >= 1 and <= 10 && InitialRetryDelaySeconds is >= 1 and <= 3600
        && MaxRetryDelaySeconds >= InitialRetryDelaySeconds && MaxRetryDelaySeconds <= 86400;
    public TimeSpan Backoff(int failures) => TimeSpan.FromSeconds(Math.Min(MaxRetryDelaySeconds,
        InitialRetryDelaySeconds * Math.Pow(4, Math.Clamp(failures - 1, 0, 10))));
}
