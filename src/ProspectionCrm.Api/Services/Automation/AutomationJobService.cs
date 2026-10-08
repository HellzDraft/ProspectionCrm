using Microsoft.EntityFrameworkCore;
using ProspectionCrm.Api.Data;
using ProspectionCrm.Api.Dtos.AutomationJobs;

namespace ProspectionCrm.Api.Services.Automation;

public sealed class AutomationJobService(ProspectionCrmDbContext db, ICurrentWorkspaceProvider workspaceProvider,
    IAutomationJobQueue queue)
{
    public async Task<AutomationJobResult<AutomationJobDto>> EnqueueAsync(EnqueueAutomationJobRequest request, CancellationToken token)
    {
        var workspace = await WorkspaceAsync(token);
        if (workspace is null) return Unavailable<AutomationJobDto>();
        var result = await queue.EnqueueAsync(workspace.Value, request, token);
        return new(result.Value is null ? null : AutomationJobDto.From(result.Value), result.Status, result.Code);
    }

    public async Task<AutomationJobResult<AutomationJobDto>> GetAsync(Guid id, CancellationToken token)
    {
        var workspace = await WorkspaceAsync(token);
        if (workspace is null) return Unavailable<AutomationJobDto>();
        var job = await db.AutomationJobs.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.WorkspaceId == workspace, token);
        return job is null ? new(null, 404, "ResourceNotFound") : new(AutomationJobDto.From(job));
    }

    public async Task<AutomationJobResult<AutomationJobsPageDto>> ListAsync(int offset, int limit, string? status,
        Guid? automationRuleId, string? triggerType, string? actionCategory, CancellationToken token)
    {
        if (offset < 0 || limit is < 1 or > 200) return new(null, 400, "InvalidPagination");
        if (status is not null && !AutomationJobStatuses.IsValid(status)) return new(null, 400, "InvalidStatusCode");
        if (triggerType is not null && !AutomationJobTriggers.IsValid(triggerType)) return new(null, 400, "InvalidTriggerTypeCode");
        if (actionCategory is not null && !AutomationCategories.IsValid(actionCategory)) return new(null, 400, "InvalidActionCategoryCode");
        var workspace = await WorkspaceAsync(token);
        if (workspace is null) return Unavailable<AutomationJobsPageDto>();
        var query = db.AutomationJobs.AsNoTracking().Where(x => x.WorkspaceId == workspace);
        if (status is not null) query = query.Where(x => x.StatusCode == status);
        if (automationRuleId is not null) query = query.Where(x => x.AutomationRuleId == automationRuleId);
        if (triggerType is not null) query = query.Where(x => x.TriggerTypeCode == triggerType);
        if (actionCategory is not null) query = query.Where(x => x.ActionCategoryCode == actionCategory);
        var total = await query.CountAsync(token);
        var jobs = await query.OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id)
            .Skip(offset).Take(limit).ToListAsync(token);
        return new(new(offset, limit, total, (long)offset + jobs.Count < total, jobs.Select(AutomationJobDto.From).ToList()));
    }

    private async Task<Guid?> WorkspaceAsync(CancellationToken token)
    {
        try { return await workspaceProvider.GetCurrentWorkspaceIdAsync(token); }
        catch (InvalidOperationException) { return null; }
    }
    private static AutomationJobResult<T> Unavailable<T>() => new(default, 409, "WorkspaceUnavailable");
}
