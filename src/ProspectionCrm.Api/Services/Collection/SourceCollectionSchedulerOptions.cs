namespace ProspectionCrm.Api.Services.Collection;

public sealed class SourceCollectionSchedulerOptions
{
    public const string SectionName = "SourceCollectionScheduler";
    public bool Enabled { get; set; }
    public int PollIntervalSeconds { get; set; } = 30;
    public int BatchSize { get; set; } = 50;
    public bool IsValid() => PollIntervalSeconds is >= 1 and <= 3600 && BatchSize is >= 1 and <= 500;
}
