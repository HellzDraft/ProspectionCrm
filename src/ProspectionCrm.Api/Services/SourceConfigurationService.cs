using Microsoft.EntityFrameworkCore;
using ProspectionCrm.Api.Data;
using ProspectionCrm.Api.Dtos.SourceConfigurations;
using ProspectionCrm.Api.Entities;

namespace ProspectionCrm.Api.Services;

public class SourceConfigurationService(ProspectionCrmDbContext dbContext, ICurrentWorkspaceProvider currentWorkspaceProvider) : ISourceConfigurationService
{
    public async Task<IReadOnlyList<SourceConfigurationDto>> GetAllAsync(bool includeArchived = false, CancellationToken cancellationToken = default)
    {
        var workspaceId = await currentWorkspaceProvider.GetCurrentWorkspaceIdAsync(cancellationToken);
        var entities = await dbContext.SourceConfigurations.AsNoTracking()
            .Where(x => x.WorkspaceId == workspaceId && (includeArchived || x.ArchivedAt == null))
            .OrderBy(x => x.Name).ThenBy(x => x.Id).ToListAsync(cancellationToken);
        return entities.Select(ToDto).ToList();
    }

    public async Task<SourceConfigurationDto?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
    {
        var workspaceId = await currentWorkspaceProvider.GetCurrentWorkspaceIdAsync(cancellationToken);
        var entity = await dbContext.SourceConfigurations.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == id && x.WorkspaceId == workspaceId, cancellationToken);
        return entity is null ? null : ToDto(entity);
    }

    public async Task<(SourceConfigurationDto? SourceConfiguration, string? Error)> CreateAsync(CreateSourceConfigurationRequest request, CancellationToken cancellationToken)
    {
        var workspaceId = await currentWorkspaceProvider.GetCurrentWorkspaceIdAsync(cancellationToken);
        var error = Validate(request.ConfigurationJson);
        if (error is not null)
            return (null, error);
        var entity = new SourceConfiguration
        {
            WorkspaceId = workspaceId,
            Name = request.Name,
            SourceTypeCode = request.SourceTypeCode,
            BaseUrl = request.BaseUrl,
            ConfigurationJson = JsonObjectValidation.NormalizeOptional(request.ConfigurationJson),
            Enabled = request.Enabled
        };
        dbContext.SourceConfigurations.Add(entity);
        await dbContext.SaveChangesAsync(cancellationToken);
        return (ToDto(entity), null);
    }

    public async Task<(bool Found, string? Error)> UpdateAsync(Guid id, UpdateSourceConfigurationRequest request, CancellationToken cancellationToken)
    {
        var workspaceId = await currentWorkspaceProvider.GetCurrentWorkspaceIdAsync(cancellationToken);
        var entity = await dbContext.SourceConfigurations
            .SingleOrDefaultAsync(x => x.Id == id && x.WorkspaceId == workspaceId, cancellationToken);
        if (entity is null)
            return (false, null);
        var error = Validate(request.ConfigurationJson);
        if (error is not null)
            return (true, error);
        entity.Name = request.Name;
        entity.SourceTypeCode = request.SourceTypeCode;
        entity.BaseUrl = request.BaseUrl;
        entity.ConfigurationJson = JsonObjectValidation.NormalizeOptional(request.ConfigurationJson);
        entity.Enabled = request.Enabled;
        entity.UpdatedAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
        return (true, null);
    }

    public async Task<bool> ArchiveAsync(Guid id, CancellationToken cancellationToken)
    {
        var workspaceId = await currentWorkspaceProvider.GetCurrentWorkspaceIdAsync(cancellationToken);
        var entity = await dbContext.SourceConfigurations
            .SingleOrDefaultAsync(x => x.Id == id && x.WorkspaceId == workspaceId, cancellationToken);
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
        var entity = await dbContext.SourceConfigurations
            .SingleOrDefaultAsync(x => x.Id == id && x.WorkspaceId == workspaceId, cancellationToken);
        if (entity is null)
            return (false, null);
        entity.ArchivedAt = null;
        entity.UpdatedAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
        return (true, null);
    }

    private static string? Validate(string? configurationJson)
    {
        return JsonObjectValidation.Validate(configurationJson, "ConfigurationJson", required: false);
    }

    private static SourceConfigurationDto ToDto(SourceConfiguration entity) => new()
    {
        Id = entity.Id,
        Name = entity.Name,
        SourceTypeCode = entity.SourceTypeCode,
        BaseUrl = entity.BaseUrl,
        ConfigurationJson = entity.ConfigurationJson,
        Enabled = entity.Enabled,
        CreatedAt = entity.CreatedAt,
        UpdatedAt = entity.UpdatedAt,
        ArchivedAt = entity.ArchivedAt
    };
}
