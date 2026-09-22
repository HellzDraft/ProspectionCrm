using Microsoft.EntityFrameworkCore;
using ProspectionCrm.Api.Data;
using ProspectionCrm.Api.Dtos.CrmTasks;
using ProspectionCrm.Api.Entities;

namespace ProspectionCrm.Api.Services;

public class CrmTaskService(ProspectionCrmDbContext dbContext) : ICrmTaskService
{
    public async Task<IReadOnlyList<CrmTaskDto>> GetAllAsync(
        Guid? opportunityId, bool? isCompleted, CancellationToken cancellationToken)
    {
        var query = dbContext.CrmTasks.AsNoTracking();
        if (opportunityId.HasValue)
            query = query.Where(x => x.OpportunityId == opportunityId.Value);
        if (isCompleted.HasValue)
            query = query.Where(x => x.IsCompleted == isCompleted.Value);

        var tasks = await query
            .OrderBy(x => x.IsCompleted)
            .ThenBy(x => !x.IsCompleted && !x.DueAt.HasValue)
            .ThenBy(x => !x.IsCompleted ? x.DueAt : null)
            .ThenBy(x => x.CreatedAt).ThenBy(x => x.Id)
            .ToListAsync(cancellationToken);
        return tasks.Select(ToDto).ToList();
    }

    public async Task<CrmTaskDto?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
    {
        var task = await dbContext.CrmTasks.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        return task is null ? null : ToDto(task);
    }

    public async Task<IReadOnlyList<CrmTaskDto>> GetDueAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var tasks = await dbContext.CrmTasks.AsNoTracking()
            .Where(x => !x.IsCompleted && x.DueAt.HasValue && x.DueAt <= now)
            .OrderBy(x => x.DueAt).ThenBy(x => x.CreatedAt).ThenBy(x => x.Id)
            .ToListAsync(cancellationToken);
        return tasks.Select(ToDto).ToList();
    }

    public async Task<(CrmTaskDto? CrmTask, string? Error)> CreateAsync(
        CreateCrmTaskRequest request, CancellationToken cancellationToken)
    {
        var error = await ValidateOpportunityAsync(request.OpportunityId, cancellationToken);
        if (error is not null)
            return (null, error);

        var task = new CrmTask
        {
            Id = Guid.NewGuid(),
            OpportunityId = request.OpportunityId!.Value,
            Title = request.Title,
            Description = request.Description,
            DueAt = request.DueAt?.ToUniversalTime(),
            IsCompleted = false,
            CompletedAt = null,
            CreatedAt = DateTimeOffset.UtcNow
        };

        dbContext.CrmTasks.Add(task);
        await dbContext.SaveChangesAsync(cancellationToken);
        return (ToDto(task), null);
    }

    public async Task<(bool Found, string? Error)> UpdateAsync(
        Guid id, UpdateCrmTaskRequest request, CancellationToken cancellationToken)
    {
        var task = await dbContext.CrmTasks
            .SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (task is null)
            return (false, null);

        var error = await ValidateOpportunityAsync(request.OpportunityId, cancellationToken);
        if (error is not null)
            return (true, error);

        task.OpportunityId = request.OpportunityId!.Value;
        task.Title = request.Title;
        task.Description = request.Description;
        task.DueAt = request.DueAt?.ToUniversalTime();
        await dbContext.SaveChangesAsync(cancellationToken);
        return (true, null);
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var task = await dbContext.CrmTasks
            .SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (task is null)
            return false;

        dbContext.CrmTasks.Remove(task);
        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<bool> CompleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var task = await dbContext.CrmTasks
            .SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (task is null)
            return false;

        if (!task.IsCompleted)
        {
            task.IsCompleted = true;
            task.CompletedAt = DateTimeOffset.UtcNow;
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        return true;
    }

    public async Task<bool> ReopenAsync(Guid id, CancellationToken cancellationToken)
    {
        var task = await dbContext.CrmTasks
            .SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (task is null)
            return false;

        task.IsCompleted = false;
        task.CompletedAt = null;
        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    private async Task<string?> ValidateOpportunityAsync(
        Guid? opportunityId, CancellationToken cancellationToken)
    {
        if (!opportunityId.HasValue ||
            !await dbContext.Opportunities.AnyAsync(x => x.Id == opportunityId.Value, cancellationToken))
            return "OpportunityId does not reference an existing opportunity.";

        return null;
    }

    private static CrmTaskDto ToDto(CrmTask task) => new()
    {
        Id = task.Id,
        OpportunityId = task.OpportunityId,
        Title = task.Title,
        Description = task.Description,
        DueAt = task.DueAt,
        IsCompleted = task.IsCompleted,
        CompletedAt = task.CompletedAt,
        CreatedAt = task.CreatedAt,
    };
}
