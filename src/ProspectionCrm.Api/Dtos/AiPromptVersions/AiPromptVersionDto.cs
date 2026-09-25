namespace ProspectionCrm.Api.Dtos.AiPromptVersions;

public class AiPromptVersionDto
{
    public Guid Id { get; set; }
    public Guid AiPromptTemplateId { get; set; }
    public int VersionNumber { get; set; }
    public string? SystemPrompt { get; set; }
    public string UserPromptTemplate { get; set; } = string.Empty;
    public string? OutputSchemaJson { get; set; }
    public Guid? DefaultModelConfigurationId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
