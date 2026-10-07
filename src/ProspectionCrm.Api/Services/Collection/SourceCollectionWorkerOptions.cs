namespace ProspectionCrm.Api.Services.Collection;

public sealed class SourceCollectionWorkerOptions
{
    public const string SectionName = "SourceCollectionWorker";
    public bool Enabled { get; set; }
    public int IdleDelaySeconds { get; set; } = 5;
    public int LeaseDurationSeconds { get; set; } = 300;
    public bool IsValid() => IdleDelaySeconds is >= 1 and <= 300 && LeaseDurationSeconds is >= 30 and <= 3600;
}
