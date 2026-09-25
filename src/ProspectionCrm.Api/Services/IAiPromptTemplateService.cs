using ProspectionCrm.Api.Dtos.AiPromptTemplates;
using ProspectionCrm.Api.Dtos.AiPromptVersions;

namespace ProspectionCrm.Api.Services;

public interface IAiPromptTemplateService
{
    Task<IReadOnlyList<AiPromptTemplateDto>> GetAllAsync(bool includeArchived = false, string? purposeCode = null, CancellationToken cancellationToken = default);
    Task<AiPromptTemplateDto?> GetByIdAsync(Guid id, CancellationToken cancellationToken);
    Task<(AiPromptTemplateDto? Item, string? Error)> CreateAsync(CreateAiPromptTemplateRequest request, CancellationToken cancellationToken);
    Task<(bool Found, string? Error)> UpdateAsync(Guid id, UpdateAiPromptTemplateRequest request, CancellationToken cancellationToken);
    Task<bool> ArchiveAsync(Guid id, CancellationToken cancellationToken);
    Task<(bool Found, string? Error)> RestoreAsync(Guid id, CancellationToken cancellationToken);
    Task<IReadOnlyList<AiPromptVersionDto>?> GetVersionsAsync(Guid templateId, CancellationToken cancellationToken);
    Task<AiPromptVersionDto?> GetVersionAsync(Guid templateId, int versionNumber, CancellationToken cancellationToken);
    Task<(bool Found, AiPromptVersionDto? Version, string? Error)> CreateVersionAsync(
        Guid templateId, CreateAiPromptVersionRequest request, CancellationToken cancellationToken);
}
