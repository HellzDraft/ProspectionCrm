using Microsoft.EntityFrameworkCore;
using ProspectionCrm.Api.Data;
using ProspectionCrm.Api.Dtos.AutomationRules;
using ProspectionCrm.Api.Entities;

namespace ProspectionCrm.Api.Services;

public class AutomationRuleService(ProspectionCrmDbContext dbContext, ICurrentWorkspaceProvider currentWorkspaceProvider)
    : IAutomationRuleService
{
    public async Task<IReadOnlyList<AutomationRuleDto>> GetAllAsync(bool includeArchived = false, Guid? pipelineId = null, bool? enabled = null, string? triggerTypeCode = null, CancellationToken cancellationToken = default)
    {
        var workspaceId = await currentWorkspaceProvider.GetCurrentWorkspaceIdAsync(cancellationToken);
        var entities = await dbContext.AutomationRules.AsNoTracking()
            .Where(x => x.WorkspaceId == workspaceId && (includeArchived || x.ArchivedAt == null)
                && (pipelineId == null || x.PipelineId == pipelineId)
                && (enabled == null || x.Enabled == enabled)
                && (triggerTypeCode == null || x.TriggerTypeCode == triggerTypeCode))
            .OrderBy(x => x.Name).ThenBy(x => x.Id).ToListAsync(cancellationToken);
        return entities.Select(ToDto).ToList();
    }

    public async Task<AutomationRuleDto?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
    {
        var workspaceId = await currentWorkspaceProvider.GetCurrentWorkspaceIdAsync(cancellationToken);
        var entity = await dbContext.AutomationRules.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == id && x.WorkspaceId == workspaceId, cancellationToken);
        return entity is null ? null : ToDto(entity);
    }

    public async Task<(AutomationRuleDto? Item, string? Error)> CreateAsync(CreateAutomationRuleRequest request, CancellationToken cancellationToken)
    {
        var workspaceId = await currentWorkspaceProvider.GetCurrentWorkspaceIdAsync(cancellationToken);
        var error = JsonObjectValidation.Validate(request.ConditionJson, nameof(request.ConditionJson))
            ?? JsonObjectValidation.Validate(request.ActionConfigurationJson, nameof(request.ActionConfigurationJson))
            ?? await RulePipelineValidation.ValidateAsync(dbContext, workspaceId, request.PipelineId,
            true, cancellationToken);
        if (error is not null)
            return (null, error);
        var entity = new AutomationRule
        {
            WorkspaceId = workspaceId,
            PipelineId = request.PipelineId,
            Name = request.Name,
            Description = request.Description,
            TriggerTypeCode = request.TriggerTypeCode,
            ConditionJson = JsonObjectValidation.NormalizeOptional(request.ConditionJson),
            ActionTypeCode = request.ActionTypeCode,
            ActionConfigurationJson = JsonObjectValidation.NormalizeOptional(request.ActionConfigurationJson),
            Enabled = request.Enabled,
        };
        dbContext.AutomationRules.Add(entity);
        await dbContext.SaveChangesAsync(cancellationToken);
        return (ToDto(entity), null);
    }

    public async Task<(bool Found, string? Error)> UpdateAsync(Guid id, UpdateAutomationRuleRequest request, CancellationToken cancellationToken)
    {
        var workspaceId = await currentWorkspaceProvider.GetCurrentWorkspaceIdAsync(cancellationToken);
        var entity = await dbContext.AutomationRules.SingleOrDefaultAsync(x => x.Id == id && x.WorkspaceId == workspaceId, cancellationToken);
        if (entity is null)
            return (false, null);
        var error = JsonObjectValidation.Validate(request.ConditionJson, nameof(request.ConditionJson))
            ?? JsonObjectValidation.Validate(request.ActionConfigurationJson, nameof(request.ActionConfigurationJson))
            ?? await RulePipelineValidation.ValidateAsync(dbContext, workspaceId, request.PipelineId,
            request.PipelineId != entity.PipelineId, cancellationToken);
        if (error is not null)
            return (true, error);
        entity.PipelineId = request.PipelineId;
        entity.Name = request.Name;
        entity.Description = request.Description;
        entity.TriggerTypeCode = request.TriggerTypeCode;
        entity.ConditionJson = JsonObjectValidation.NormalizeOptional(request.ConditionJson);
        entity.ActionTypeCode = request.ActionTypeCode;
        entity.ActionConfigurationJson = JsonObjectValidation.NormalizeOptional(request.ActionConfigurationJson);
        entity.Enabled = request.Enabled;
        entity.UpdatedAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
        return (true, null);
    }

    public async Task<bool> ArchiveAsync(Guid id, CancellationToken cancellationToken)
    {
        var workspaceId = await currentWorkspaceProvider.GetCurrentWorkspaceIdAsync(cancellationToken);
        var entity = await dbContext.AutomationRules.SingleOrDefaultAsync(x => x.Id == id && x.WorkspaceId == workspaceId, cancellationToken);
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
        var entity = await dbContext.AutomationRules.SingleOrDefaultAsync(x => x.Id == id && x.WorkspaceId == workspaceId, cancellationToken);
        if (entity is null)
            return (false, null);
        var error = await RulePipelineValidation.ValidateAsync(dbContext, workspaceId,
            entity.PipelineId, true, cancellationToken);
        if (error is not null)
            return (true, error);
        entity.ArchivedAt = null;
        entity.UpdatedAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
        return (true, null);
    }

    private static AutomationRuleDto ToDto(AutomationRule entity) => new()
    {
        Id = entity.Id,
        PipelineId = entity.PipelineId,
        Name = entity.Name,
        Description = entity.Description,
        TriggerTypeCode = entity.TriggerTypeCode,
        ConditionJson = entity.ConditionJson,
        ActionTypeCode = entity.ActionTypeCode,
        ActionConfigurationJson = entity.ActionConfigurationJson,
        Enabled = entity.Enabled,
        CreatedAt = entity.CreatedAt,
        UpdatedAt = entity.UpdatedAt,
        ArchivedAt = entity.ArchivedAt
    };
}
