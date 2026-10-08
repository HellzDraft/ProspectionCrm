using Microsoft.EntityFrameworkCore;
using ProspectionCrm.Api.Data;
using ProspectionCrm.Api.Entities;

namespace ProspectionCrm.Api.Services.Automation;

public sealed record AutomationActionResult(bool Succeeded, string ReasonCode, Guid? EntityId = null);
public interface IAutomationActionExecutor
{
    Task<AutomationActionResult> ExecuteAsync(AutomationJob job, CreateCrmTaskPlan plan,
        DateTimeOffset now, CancellationToken token);
}

// Called only inside the processor's short, fenced transaction. SaveChanges belongs to the processor.
public sealed class CreateCrmTaskAutomationExecutor(ProspectionCrmDbContext db, AutomationRuntimeStore runtime) : IAutomationActionExecutor
{
    public async Task<AutomationActionResult> ExecuteAsync(AutomationJob job, CreateCrmTaskPlan plan,
        DateTimeOffset now, CancellationToken token)
    {
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("An automation mutation requires a transaction.");
        var existing = await db.CrmTasks.AsNoTracking().Where(x => x.AutomationJobId == job.Id)
            .Select(x => new { x.Id, x.Opportunity.WorkspaceId }).SingleOrDefaultAsync(token);
        if (existing is not null)
            return existing.WorkspaceId == job.WorkspaceId
                ? new(true, "effect-already-applied", existing.Id) : new(false, "workspace-mismatch");
        var opportunity = await db.Opportunities.FromSqlInterpolated($"""
            SELECT * FROM "Opportunities" WHERE "Id" = {plan.OpportunityId}
                AND "WorkspaceId" = {job.WorkspaceId} FOR UPDATE
            """).AsNoTracking().SingleOrDefaultAsync(token);
        if (opportunity is null) return new(false, "opportunity-not-found");
        if (opportunity.ArchivedAt is not null) return new(false, "opportunity-archived");
        await runtime.EnsureLeaseAsync(job, job.LeaseOwner!, token);
        now = runtime.Now;
        var task = new CrmTask { AutomationJobId = job.Id, OpportunityId = opportunity.Id,
            Title = plan.Title, Description = plan.Description, CreatedAt = now,
            DueAt = plan.DueInDays is { } days ? now.AddDays(days) : null, IsCompleted = false };
        db.CrmTasks.Add(task);
        return new(true, "task-created", task.Id);
    }
}
