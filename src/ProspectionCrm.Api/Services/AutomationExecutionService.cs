using Microsoft.EntityFrameworkCore;
using ProspectionCrm.Api.Data;
using ProspectionCrm.Api.Dtos.AutomationExecutions;
using ProspectionCrm.Api.Entities;

namespace ProspectionCrm.Api.Services;

public class AutomationExecutionService(ProspectionCrmDbContext dbContext, ICurrentWorkspaceProvider currentWorkspaceProvider)
    : IAutomationExecutionService
{
    public async Task<(IReadOnlyList<AutomationExecutionDto>? Items, string? Error)> GetAllAsync(Guid? automationRuleId = null, string? statusCode = null, DateTimeOffset? from = null, DateTimeOffset? to = null, CancellationToken cancellationToken = default)
    {
        if (from.HasValue && to.HasValue && from > to)
            return (null, "From must be less than or equal to To.");
        if (statusCode is not null && statusCode is not ("pending" or "running" or "succeeded" or "failed" or "cancelled" or "skipped"))
            return (null, "StatusCode must be pending, running, succeeded, failed, cancelled or skipped.");
        var workspaceId = await currentWorkspaceProvider.GetCurrentWorkspaceIdAsync(cancellationToken);
        from = from?.ToUniversalTime();
        to = to?.ToUniversalTime();
        var entities = await dbContext.AutomationExecutions.AsNoTracking()
            .Where(x => x.WorkspaceId == workspaceId
                && (automationRuleId == null || x.AutomationRuleId == automationRuleId)
                && (statusCode == null || x.StatusCode == statusCode)
                && (from == null || x.TriggeredAt >= from)
                && (to == null || x.TriggeredAt <= to))
            .OrderByDescending(x => x.TriggeredAt).ThenBy(x => x.Id).ToListAsync(cancellationToken);
        return (entities.Select(ToDto).ToList(), null);
    }

    public async Task<AutomationExecutionDto?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
    {
        var workspaceId = await currentWorkspaceProvider.GetCurrentWorkspaceIdAsync(cancellationToken);
        var entity = await dbContext.AutomationExecutions.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == id && x.WorkspaceId == workspaceId, cancellationToken);
        return entity is null ? null : ToDto(entity);
    }

    private static AutomationExecutionDto ToDto(AutomationExecution entity) => new()
    {
        Id = entity.Id,
        AutomationRuleId = entity.AutomationRuleId,
        StatusCode = entity.StatusCode,
        TriggeredAt = entity.TriggeredAt,
        StartedAt = entity.StartedAt,
        FinishedAt = entity.FinishedAt,
        ErrorMessage = entity.ErrorMessage,
        ContextJson = entity.ContextJson
    };
}
