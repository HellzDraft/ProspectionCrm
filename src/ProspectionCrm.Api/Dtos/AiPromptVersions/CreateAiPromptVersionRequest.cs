using System.ComponentModel.DataAnnotations;

namespace ProspectionCrm.Api.Dtos.AiPromptVersions;

public class CreateAiPromptVersionRequest
{
    public string? SystemPrompt { get; set; }

    [Required]
    public string UserPromptTemplate { get; set; } = string.Empty;

    public string? OutputSchemaJson { get; set; }

    public Guid? DefaultModelConfigurationId { get; set; }
}
