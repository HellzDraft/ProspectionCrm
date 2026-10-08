using System.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using ProspectionCrm.Api.Data;
using ProspectionCrm.Api.Dtos.AutomationJobs;

namespace ProspectionCrm.Api.Services.Automation;

public sealed record AutomationEvent(Guid WorkspaceId, string TriggerTypeCode, string EventKey,
    Guid? PipelineId = null, string? PayloadJson = null);
public sealed record DispatchedAutomationJob(Guid AutomationRuleId, Guid JobId, bool IsCreated);
public sealed record AutomationDispatchSummary(string TriggerTypeCode, string EventKey, int CandidateRuleCount,
    int CreatedCount, int ExistingCount, IReadOnlyList<DispatchedAutomationJob> Jobs);

public interface IAutomationEventDispatcher
{
    Task<AutomationJobResult<AutomationDispatchSummary>> DispatchAsync(AutomationEvent input, CancellationToken token);
}

public sealed class AutomationEventDispatcher(ProspectionCrmDbContext db, IAutomationJobQueue queue) : IAutomationEventDispatcher
{
    public async Task<AutomationJobResult<AutomationDispatchSummary>> DispatchAsync(AutomationEvent input, CancellationToken token)
    {
        if (!AutomationJobTriggers.IsValid(input.TriggerTypeCode)) return new(null, 400, AutomationEvaluationReasons.UnsupportedTrigger);
        var context = AutomationEventContext.Create(input.EventKey, input.PipelineId, input.PayloadJson, out var error);
        if (error is not null) return new(null, 400, error);
        if (input.PipelineId is { } pipelineId && !await db.Pipelines.AsNoTracking()
            .AnyAsync(x => x.Id == pipelineId && x.WorkspaceId == input.WorkspaceId, token))
            return new(null, 404, AutomationEvaluationReasons.PipelineNotFound);
        var rules = await db.AutomationRules.AsNoTracking().Where(x => x.WorkspaceId == input.WorkspaceId
            && x.Enabled && x.ArchivedAt == null && x.TriggerTypeCode == input.TriggerTypeCode
            && (x.PipelineId == null || x.PipelineId == input.PipelineId)).OrderBy(x => x.Id).ToListAsync(token);
        // Resolve all categories before writing. Unsupported historical actions cannot be assigned an invented category.
        if (rules.Any(x => AutomationActionCatalog.Find(x.ActionTypeCode) is null))
            return new(null, 409, AutomationEvaluationReasons.UnsupportedAction);
        var jobs = new List<DispatchedAutomationJob>();
        if (rules.Count > 0)
        {
            // Enqueue shares this scoped DbContext. READ COMMITTED gives its post-conflict read a fresh snapshot.
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, token);
            try
            {
                foreach (var rule in rules)
                {
                    var category = AutomationActionCatalog.Find(rule.ActionTypeCode)!.CategoryCode;
                    var result = await queue.EnqueueAsync(input.WorkspaceId, new EnqueueAutomationJobRequest
                    {
                        AutomationRuleId = rule.Id, TriggerTypeCode = input.TriggerTypeCode, ActionCategoryCode = category,
                        TriggerKey = AutomationEventContext.TriggerKey(input.EventKey, rule.Id), ContextJson = context
                    }, token);
                    if (result.Value is not { } job) return new(null, result.Status, result.Code);
                    // The administration API can occupy a key. Never report an unrelated job as a successful dispatch.
                    if (job.AutomationRuleId != rule.Id || job.ActionCategoryCode != category
                        || !AutomationEventContext.TryParse(job.ContextJson, out var existing) || existing.EventKey != input.EventKey)
                        return new(null, 409, AutomationEvaluationReasons.DispatchKeyConflict);
                    jobs.Add(new(rule.Id, job.Id, result.Status == 201));
                }
                await transaction.CommitAsync(token);
            }
            catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.DeadlockDetected or PostgresErrorCodes.SerializationFailure)
            { return new(null, 409, AutomationEvaluationReasons.ConcurrentDispatchChange); }
        }
        return new(new(input.TriggerTypeCode, input.EventKey, rules.Count, jobs.Count(x => x.IsCreated),
            jobs.Count(x => !x.IsCreated), jobs));
    }
}
