namespace ProspectionCrm.Api.Services.Collection;

public sealed class RssCollectionOptions
{
    public const string SectionName = "RssCollection";
    public int TimeoutSeconds { get; set; } = 15;
    public int MaxResponseBytes { get; set; } = 2_000_000;
    public int MaxRedirects { get; set; } = 3;
    public int DefaultMaxItems { get; set; } = 100;
    public string UserAgent { get; set; } = "ProspectionCrm/1.0";
    public bool IsValid() => TimeoutSeconds is >= 1 and <= 60
        && MaxResponseBytes is >= 1024 and <= 2_000_000 && MaxRedirects is >= 0 and <= 3
        && DefaultMaxItems is >= 1 and <= 100 && UserAgent == "ProspectionCrm/1.0";
}
