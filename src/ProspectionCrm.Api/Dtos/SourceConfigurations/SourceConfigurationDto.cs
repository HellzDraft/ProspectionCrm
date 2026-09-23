namespace ProspectionCrm.Api.Dtos.SourceConfigurations;

public class SourceConfigurationDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string SourceTypeCode { get; set; } = string.Empty;
    public string? BaseUrl { get; set; }
    public string? ConfigurationJson { get; set; }
    public bool Enabled { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public DateTimeOffset? ArchivedAt { get; set; }
}
