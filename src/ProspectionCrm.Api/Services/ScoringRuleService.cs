using Microsoft.EntityFrameworkCore;
using ProspectionCrm.Api.Data;
using ProspectionCrm.Api.Dtos.ScoringRules;
using ProspectionCrm.Api.Entities;

namespace ProspectionCrm.Api.Services;

public class ScoringRuleService(ProspectionCrmDbContext dbContext, ICurrentWorkspaceProvider currentWorkspaceProvider)
    : IScoringRuleService
{
    public async Task<IReadOnlyList<ScoringRuleDto>> GetAllAsync(bool includeArchived = false, Guid? pipelineId = null, bool? enabled = null, CancellationToken cancellationToken = default)
    {
        var workspaceId = await currentWorkspaceProvider.GetCurrentWorkspaceIdAsync(cancellationToken);
        var entities = await dbContext.ScoringRules.AsNoTracking()
            .Where(x => x.WorkspaceId == workspaceId && (includeArchived || x.ArchivedAt == null)
                && (pipelineId == null || x.PipelineId == pipelineId)
                && (enabled == null || x.Enabled == enabled))
            .OrderBy(x => x.Name).ThenBy(x => x.Id).ToListAsync(cancellationToken);
        return entities.Select(ToDto).ToList();
    }

    public async Task<ScoringRuleDto?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
    {
        var workspaceId = await currentWorkspaceProvider.GetCurrentWorkspaceIdAsync(cancellationToken);
        var entity = await dbContext.ScoringRules.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == id && x.WorkspaceId == workspaceId, cancellationToken);
        return entity is null ? null : ToDto(entity);
    }

    public async Task<(ScoringRuleDto? Item, string? Error)> CreateAsync(CreateScoringRuleRequest request, CancellationToken cancellationToken)
    {
        var workspaceId = await currentWorkspaceProvider.GetCurrentWorkspaceIdAsync(cancellationToken);
        if (request.Weight < -100m || request.Weight > 100m)
            return (null, "Weight must be between -100 and 100.");
        var error = JsonObjectValidation.Validate(request.ConfigurationJson, nameof(request.ConfigurationJson), true)
            ?? await RulePipelineValidation.ValidateAsync(dbContext, workspaceId, request.PipelineId,
            true, cancellationToken);
        if (error is not null)
            return (null, error);
        var entity = new ScoringRule
        {
            WorkspaceId = workspaceId,
            PipelineId = request.PipelineId,
            Name = request.Name,
            Description = request.Description,
            RuleTypeCode = request.RuleTypeCode,
            Weight = request.Weight,
            ConfigurationJson = request.ConfigurationJson,
            Enabled = request.Enabled,
        };
        dbContext.ScoringRules.Add(entity);
        await dbContext.SaveChangesAsync(cancellationToken);
        return (ToDto(entity), null);
    }

    public async Task<(bool Found, string? Error)> UpdateAsync(Guid id, UpdateScoringRuleRequest request, CancellationToken cancellationToken)
    {
        var workspaceId = await currentWorkspaceProvider.GetCurrentWorkspaceIdAsync(cancellationToken);
        var entity = await dbContext.ScoringRules.SingleOrDefaultAsync(x => x.Id == id && x.WorkspaceId == workspaceId, cancellationToken);
        if (entity is null)
            return (false, null);
        if (request.Weight < -100m || request.Weight > 100m)
            return (true, "Weight must be between -100 and 100.");
        var error = JsonObjectValidation.Validate(request.ConfigurationJson, nameof(request.ConfigurationJson), true)
            ?? await RulePipelineValidation.ValidateAsync(dbContext, workspaceId, request.PipelineId,
            request.PipelineId != entity.PipelineId, cancellationToken);
        if (error is not null)
            return (true, error);
        entity.PipelineId = request.PipelineId;
        entity.Name = request.Name;
        entity.Description = request.Description;
        entity.RuleTypeCode = request.RuleTypeCode;
        entity.Weight = request.Weight;
        entity.ConfigurationJson = request.ConfigurationJson;
        entity.Enabled = request.Enabled;
        entity.UpdatedAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
        return (true, null);
    }

    public async Task<bool> ArchiveAsync(Guid id, CancellationToken cancellationToken)
    {
        var workspaceId = await currentWorkspaceProvider.GetCurrentWorkspaceIdAsync(cancellationToken);
        var entity = await dbContext.ScoringRules.SingleOrDefaultAsync(x => x.Id == id && x.WorkspaceId == workspaceId, cancellationToken);
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
        var entity = await dbContext.ScoringRules.SingleOrDefaultAsync(x => x.Id == id && x.WorkspaceId == workspaceId, cancellationToken);
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

    private static ScoringRuleDto ToDto(ScoringRule entity) => new()
    {
        Id = entity.Id,
        PipelineId = entity.PipelineId,
        Name = entity.Name,
        Description = entity.Description,
        RuleTypeCode = entity.RuleTypeCode,
        Weight = entity.Weight,
        ConfigurationJson = entity.ConfigurationJson,
        Enabled = entity.Enabled,
        CreatedAt = entity.CreatedAt,
        UpdatedAt = entity.UpdatedAt,
        ArchivedAt = entity.ArchivedAt
    };
}
