using System.ComponentModel.DataAnnotations;

namespace ProspectionCrm.Api.Dtos.AiPromptTemplates;

public class CreateAiPromptTemplateRequest
{
    [Required]
    [MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    [Required]
    [MaxLength(50)]
    public string PurposeCode { get; set; } = string.Empty;

    [MaxLength(5000)]
    public string? Description { get; set; }
}
