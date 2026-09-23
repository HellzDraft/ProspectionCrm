using Microsoft.EntityFrameworkCore;
using ProspectionCrm.Api.Data;
using ProspectionCrm.Api.Dtos.SavedSearches;
using ProspectionCrm.Api.Entities;

namespace ProspectionCrm.Api.Services;

public class SavedSearchService(ProspectionCrmDbContext dbContext, ICurrentWorkspaceProvider currentWorkspaceProvider) : ISavedSearchService
{
    public async Task<IReadOnlyList<SavedSearchDto>> GetAllAsync(bool includeArchived = false, Guid? pipelineId = null, Guid? sourceConfigurationId = null, bool? enabled = null, CancellationToken cancellationToken = default)
    {
        var workspaceId = await currentWorkspaceProvider.GetCurrentWorkspaceIdAsync(cancellationToken);
        var entities = await dbContext.SavedSearches.AsNoTracking()
            .Where(x => x.WorkspaceId == workspaceId && (includeArchived || x.ArchivedAt == null))
            .Where(x => (!pipelineId.HasValue || x.PipelineId == pipelineId.Value)
                && (!sourceConfigurationId.HasValue || x.SourceConfigurationId == sourceConfigurationId.Value)
                && (!enabled.HasValue || x.Enabled == enabled.Value))
            .OrderBy(x => x.Name).ThenBy(x => x.Id).ToListAsync(cancellationToken);
        return entities.Select(ToDto).ToList();
    }

    public async Task<SavedSearchDto?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
    {
        var workspaceId = await currentWorkspaceProvider.GetCurrentWorkspaceIdAsync(cancellationToken);
        var entity = await dbContext.SavedSearches.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == id && x.WorkspaceId == workspaceId, cancellationToken);
        return entity is null ? null : ToDto(entity);
    }

    public async Task<(SavedSearchDto? SavedSearch, string? Error)> CreateAsync(CreateSavedSearchRequest request, CancellationToken cancellationToken)
    {
        var workspaceId = await currentWorkspaceProvider.GetCurrentWorkspaceIdAsync(cancellationToken);
        var error = await ValidateAsync(workspaceId, request.PipelineId, request.SourceConfigurationId, request.CriteriaJson, true, true, cancellationToken);
        if (error is not null)
            return (null, error);
        var entity = new SavedSearch
        {
            WorkspaceId = workspaceId,
            PipelineId = request.PipelineId!.Value,
            SourceConfigurationId = request.SourceConfigurationId!.Value,
            Name = request.Name,
            SearchUrl = request.SearchUrl,
            CriteriaJson = request.CriteriaJson,
            Enabled = request.Enabled
        };
        dbContext.SavedSearches.Add(entity);
        await dbContext.SaveChangesAsync(cancellationToken);
        return (ToDto(entity), null);
    }

    public async Task<(bool Found, string? Error)> UpdateAsync(Guid id, UpdateSavedSearchRequest request, CancellationToken cancellationToken)
    {
        var workspaceId = await currentWorkspaceProvider.GetCurrentWorkspaceIdAsync(cancellationToken);
        var entity = await dbContext.SavedSearches
            .SingleOrDefaultAsync(x => x.Id == id && x.WorkspaceId == workspaceId, cancellationToken);
        if (entity is null)
            return (false, null);
        var error = await ValidateAsync(workspaceId, request.PipelineId, request.SourceConfigurationId, request.CriteriaJson, request.PipelineId != entity.PipelineId, request.SourceConfigurationId != entity.SourceConfigurationId, cancellationToken);
        if (error is not null)
            return (true, error);
        entity.PipelineId = request.PipelineId!.Value;
        entity.SourceConfigurationId = request.SourceConfigurationId!.Value;
        entity.Name = request.Name;
        entity.SearchUrl = request.SearchUrl;
        entity.CriteriaJson = request.CriteriaJson;
        entity.Enabled = request.Enabled;
        entity.UpdatedAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
        return (true, null);
    }

    public async Task<bool> ArchiveAsync(Guid id, CancellationToken cancellationToken)
    {
        var workspaceId = await currentWorkspaceProvider.GetCurrentWorkspaceIdAsync(cancellationToken);
        var entity = await dbContext.SavedSearches
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
        var entity = await dbContext.SavedSearches
            .SingleOrDefaultAsync(x => x.Id == id && x.WorkspaceId == workspaceId, cancellationToken);
        if (entity is null)
            return (false, null);
        var error = await ValidateAsync(workspaceId, entity.PipelineId, entity.SourceConfigurationId, entity.CriteriaJson, true, true, cancellationToken);
        if (error is not null)
            return (true, error);
        entity.ArchivedAt = null;
        entity.UpdatedAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
        return (true, null);
    }

    private async Task<string?> ValidateAsync(Guid workspaceId, Guid? pipelineId, Guid? sourceConfigurationId, string criteriaJson, bool requireActivePipeline, bool requireActiveSource, CancellationToken cancellationToken)
    {
        var error = AcquisitionValidation.ValidateJsonObject(criteriaJson, "CriteriaJson", required: true);
        if (error is not null)
            return error;
        if (!pipelineId.HasValue || !await dbContext.Pipelines.AnyAsync(x => x.Id == pipelineId.Value
                && x.WorkspaceId == workspaceId && (!requireActivePipeline || x.ArchivedAt == null), cancellationToken))
            return "PipelineId must reference a pipeline in the current workspace, active for a new selection or restore.";
        if (!sourceConfigurationId.HasValue || !await dbContext.SourceConfigurations.AnyAsync(x => x.Id == sourceConfigurationId.Value
                && x.WorkspaceId == workspaceId && (!requireActiveSource || x.ArchivedAt == null), cancellationToken))
            return "SourceConfigurationId must reference a configuration in the current workspace, active for a new selection or restore.";
        return null;
    }

    private static SavedSearchDto ToDto(SavedSearch entity) => new()
    {
        Id = entity.Id,
        PipelineId = entity.PipelineId,
        SourceConfigurationId = entity.SourceConfigurationId,
        Name = entity.Name,
        SearchUrl = entity.SearchUrl,
        CriteriaJson = entity.CriteriaJson,
        Enabled = entity.Enabled,
        CreatedAt = entity.CreatedAt,
        UpdatedAt = entity.UpdatedAt,
        ArchivedAt = entity.ArchivedAt
    };
}
