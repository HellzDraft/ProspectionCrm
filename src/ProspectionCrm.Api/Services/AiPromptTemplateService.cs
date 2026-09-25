using Microsoft.EntityFrameworkCore;
using Npgsql;
using ProspectionCrm.Api.Dtos.AiPromptVersions;
using ProspectionCrm.Api.Data;
using ProspectionCrm.Api.Dtos.AiPromptTemplates;
using ProspectionCrm.Api.Entities;

namespace ProspectionCrm.Api.Services;

public class AiPromptTemplateService(ProspectionCrmDbContext dbContext, ICurrentWorkspaceProvider currentWorkspaceProvider)
    : IAiPromptTemplateService
{
    public async Task<IReadOnlyList<AiPromptTemplateDto>> GetAllAsync(bool includeArchived = false, string? purposeCode = null, CancellationToken cancellationToken = default)
    {
        var workspaceId = await currentWorkspaceProvider.GetCurrentWorkspaceIdAsync(cancellationToken);
        var entities = await dbContext.AiPromptTemplates.AsNoTracking()
            .Where(x => x.WorkspaceId == workspaceId && (includeArchived || x.ArchivedAt == null)
                && (purposeCode == null || x.PurposeCode == purposeCode))
            .OrderBy(x => x.Name).ThenBy(x => x.Id).ToListAsync(cancellationToken);
        return entities.Select(ToDto).ToList();
    }

    public async Task<AiPromptTemplateDto?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
    {
        var workspaceId = await currentWorkspaceProvider.GetCurrentWorkspaceIdAsync(cancellationToken);
        var entity = await dbContext.AiPromptTemplates.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == id && x.WorkspaceId == workspaceId, cancellationToken);
        return entity is null ? null : ToDto(entity);
    }

    public async Task<(AiPromptTemplateDto? Item, string? Error)> CreateAsync(CreateAiPromptTemplateRequest request, CancellationToken cancellationToken)
    {
        var workspaceId = await currentWorkspaceProvider.GetCurrentWorkspaceIdAsync(cancellationToken);

        var entity = new AiPromptTemplate
        {
            WorkspaceId = workspaceId,
            Name = request.Name,
            PurposeCode = request.PurposeCode,
            Description = request.Description,
        };
        dbContext.AiPromptTemplates.Add(entity);
        await dbContext.SaveChangesAsync(cancellationToken);
        return (ToDto(entity), null);
    }

    public async Task<(bool Found, string? Error)> UpdateAsync(Guid id, UpdateAiPromptTemplateRequest request, CancellationToken cancellationToken)
    {
        var workspaceId = await currentWorkspaceProvider.GetCurrentWorkspaceIdAsync(cancellationToken);
        var entity = await dbContext.AiPromptTemplates.SingleOrDefaultAsync(x => x.Id == id && x.WorkspaceId == workspaceId, cancellationToken);
        if (entity is null)
            return (false, null);

        entity.Name = request.Name;
        entity.PurposeCode = request.PurposeCode;
        entity.Description = request.Description;
        entity.UpdatedAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
        return (true, null);
    }

    public async Task<bool> ArchiveAsync(Guid id, CancellationToken cancellationToken)
    {
        var workspaceId = await currentWorkspaceProvider.GetCurrentWorkspaceIdAsync(cancellationToken);
        var entity = await dbContext.AiPromptTemplates.SingleOrDefaultAsync(x => x.Id == id && x.WorkspaceId == workspaceId, cancellationToken);
        if (entity is null)
            return false;
        var now = DateTimeOffset.UtcNow;
        entity.ArchivedAt = now;
        entity.UpdatedAt = now;
        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<(bool Found, string? Error)> RestoreAsync(Guid id, CancellationToken cancellationToken)
    {
        var workspaceId = await currentWorkspaceProvider.GetCurrentWorkspaceIdAsync(cancellationToken);
        var entity = await dbContext.AiPromptTemplates.SingleOrDefaultAsync(x => x.Id == id && x.WorkspaceId == workspaceId, cancellationToken);
        if (entity is null)
            return (false, null);
        entity.ArchivedAt = null;
        entity.UpdatedAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
        return (true, null);
    }

    private static AiPromptTemplateDto ToDto(AiPromptTemplate entity) => new()
    {
        Id = entity.Id,
        Name = entity.Name,
        PurposeCode = entity.PurposeCode,
        Description = entity.Description,
        CreatedAt = entity.CreatedAt,
        UpdatedAt = entity.UpdatedAt,
        ArchivedAt = entity.ArchivedAt
    };

    public async Task<IReadOnlyList<AiPromptVersionDto>?> GetVersionsAsync(Guid templateId, CancellationToken cancellationToken)
    {
        var workspaceId = await currentWorkspaceProvider.GetCurrentWorkspaceIdAsync(cancellationToken);
        if (!await dbContext.AiPromptTemplates.AnyAsync(x => x.Id == templateId && x.WorkspaceId == workspaceId, cancellationToken))
            return null;

        var versions = await dbContext.AiPromptVersions.AsNoTracking()
            .Where(x => x.AiPromptTemplateId == templateId && x.AiPromptTemplate.WorkspaceId == workspaceId)
            .OrderByDescending(x => x.VersionNumber).ToListAsync(cancellationToken);
        return versions.Select(ToVersionDto).ToList();
    }

    public async Task<AiPromptVersionDto?> GetVersionAsync(Guid templateId, int versionNumber, CancellationToken cancellationToken)
    {
        var workspaceId = await currentWorkspaceProvider.GetCurrentWorkspaceIdAsync(cancellationToken);
        var version = await dbContext.AiPromptVersions.AsNoTracking()
            .SingleOrDefaultAsync(x => x.AiPromptTemplateId == templateId && x.VersionNumber == versionNumber
                && x.AiPromptTemplate.WorkspaceId == workspaceId, cancellationToken);
        return version is null ? null : ToVersionDto(version);
    }

    public async Task<(bool Found, AiPromptVersionDto? Version, string? Error)> CreateVersionAsync(
        Guid templateId, CreateAiPromptVersionRequest request, CancellationToken cancellationToken)
    {
        var workspaceId = await currentWorkspaceProvider.GetCurrentWorkspaceIdAsync(cancellationToken);
        var template = await dbContext.AiPromptTemplates.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == templateId && x.WorkspaceId == workspaceId, cancellationToken);
        if (template is null)
            return (false, null, null);
        if (template.ArchivedAt.HasValue)
            return (true, null, "Cannot add a version to an archived prompt template.");
        if (string.IsNullOrWhiteSpace(request.UserPromptTemplate))
            return (true, null, "UserPromptTemplate is required.");
        var error = JsonObjectValidation.Validate(request.OutputSchemaJson, nameof(request.OutputSchemaJson));
        if (error is not null)
            return (true, null, error);
        if (request.DefaultModelConfigurationId.HasValue)
        {
            var model = await dbContext.AiModelConfigurations.AsNoTracking()
                .SingleOrDefaultAsync(x => x.Id == request.DefaultModelConfigurationId && x.WorkspaceId == workspaceId, cancellationToken);
            if (model is null || model.ArchivedAt.HasValue || !model.Enabled)
                return (true, null, "DefaultModelConfigurationId must reference an active, enabled model in the template workspace.");
        }

        var latest = await dbContext.AiPromptVersions
            .Where(x => x.AiPromptTemplateId == templateId && x.AiPromptTemplate.WorkspaceId == workspaceId)
            .MaxAsync(x => (int?)x.VersionNumber, cancellationToken) ?? 0;
        if (latest == int.MaxValue)
            return (true, null, "The prompt template has reached its maximum version number.");
        var version = new AiPromptVersion
        {
            AiPromptTemplateId = templateId,
            VersionNumber = latest + 1,
            SystemPrompt = request.SystemPrompt,
            UserPromptTemplate = request.UserPromptTemplate,
            OutputSchemaJson = JsonObjectValidation.NormalizeOptional(request.OutputSchemaJson),
            DefaultModelConfigurationId = request.DefaultModelConfigurationId
        };
        dbContext.AiPromptVersions.Add(version);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException
        { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "UX_AiPromptVersions_Template_Version" })
        {
            dbContext.Entry(version).State = EntityState.Detached;
            return (true, null, "The prompt version changed concurrently. Please retry.");
        }
        return (true, ToVersionDto(version), null);
    }

    private static AiPromptVersionDto ToVersionDto(AiPromptVersion entity) => new()
    {
        Id = entity.Id,
        AiPromptTemplateId = entity.AiPromptTemplateId,
        VersionNumber = entity.VersionNumber,
        SystemPrompt = entity.SystemPrompt,
        UserPromptTemplate = entity.UserPromptTemplate,
        OutputSchemaJson = entity.OutputSchemaJson,
        DefaultModelConfigurationId = entity.DefaultModelConfigurationId,
        CreatedAt = entity.CreatedAt
    };
}
