using Microsoft.EntityFrameworkCore;
using ProspectionCrm.Api.Data;
using ProspectionCrm.Api.Dtos.SourceExecutions;
using ProspectionCrm.Api.Entities;

namespace ProspectionCrm.Api.Services;

public class SourceExecutionService(ProspectionCrmDbContext dbContext, ICurrentWorkspaceProvider currentWorkspaceProvider)
    : ISourceExecutionService
{
    public async Task<IReadOnlyList<SourceExecutionDto>> GetAllAsync(Guid? sourceConfigurationId = null,
        Guid? savedSearchId = null, string? statusCode = null, CancellationToken cancellationToken = default)
    {
        var workspaceId = await currentWorkspaceProvider.GetCurrentWorkspaceIdAsync(cancellationToken);
        var executions = await dbContext.SourceExecutions.AsNoTracking()
            .Where(x => x.WorkspaceId == workspaceId
                && (!sourceConfigurationId.HasValue || x.SourceConfigurationId == sourceConfigurationId.Value)
                && (!savedSearchId.HasValue || x.SavedSearchId == savedSearchId.Value)
                && (statusCode == null || x.StatusCode == statusCode))
            .OrderByDescending(x => x.StartedAt).ThenBy(x => x.Id).ToListAsync(cancellationToken);
        return executions.Select(ToDto).ToList();
    }

    public async Task<SourceExecutionDto?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
    {
        var workspaceId = await currentWorkspaceProvider.GetCurrentWorkspaceIdAsync(cancellationToken);
        var execution = await dbContext.SourceExecutions.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == id && x.WorkspaceId == workspaceId, cancellationToken);
        return execution is null ? null : ToDto(execution);
    }

    private static SourceExecutionDto ToDto(SourceExecution entity) => new()
    {
        Id = entity.Id,
        SourceConfigurationId = entity.SourceConfigurationId,
        SavedSearchId = entity.SavedSearchId,
        TriggerTypeCode = entity.TriggerTypeCode,
        StatusCode = entity.StatusCode,
        StartedAt = entity.StartedAt,
        FinishedAt = entity.FinishedAt,
        ItemsFound = entity.ItemsFound,
        ItemsCreated = entity.ItemsCreated,
        ItemsUpdated = entity.ItemsUpdated,
        ItemsIgnored = entity.ItemsIgnored,
        ErrorMessage = entity.ErrorMessage
    };
}
