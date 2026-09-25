using System.ComponentModel.DataAnnotations;

namespace ProspectionCrm.Api.Dtos.AiModelConfigurations;

public class UpdateAiModelConfigurationRequest
{
    [Required]
    [MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    [Required]
    [MaxLength(50)]
    public string ProviderCode { get; set; } = string.Empty;

    [Required]
    [MaxLength(200)]
    public string ModelName { get; set; } = string.Empty;

    public bool IsDefault { get; set; }

    public bool Enabled { get; set; } = true;
}
