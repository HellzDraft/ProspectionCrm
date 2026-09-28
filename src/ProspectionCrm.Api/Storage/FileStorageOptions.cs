namespace ProspectionCrm.Api.Storage;

public sealed class FileStorageOptions
{
    public const string SectionName = "FileStorage";

    public string RootPath { get; set; } = string.Empty;
}
