namespace ProspectionCrm.Api.Entities;

public class AiPromptVersion
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AiPromptTemplateId { get; set; }
    public int VersionNumber { get; set; }
    public string? SystemPrompt { get; set; }
    public required string UserPromptTemplate { get; set; }
    public string? OutputSchemaJson { get; set; }
    public Guid? DefaultModelConfigurationId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public AiPromptTemplate AiPromptTemplate { get; set; } = null!;
    public AiModelConfiguration? DefaultModelConfiguration { get; set; }
}
