using System.ComponentModel.DataAnnotations;

namespace ProspectionCrm.Api.Dtos.SourceConfigurations;

public class UpdateSourceConfigurationRequest
{
    [Required]
    [MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    [Required]
    [MaxLength(50)]
    public string SourceTypeCode { get; set; } = string.Empty;

    [MaxLength(2048)]
    public string? BaseUrl { get; set; }

    public string? ConfigurationJson { get; set; }

    public bool Enabled { get; set; } = true;
}
