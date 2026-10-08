using Microsoft.EntityFrameworkCore;
using ProspectionCrm.Api.Data;
using ProspectionCrm.Api.Dtos.AutomationEvents;

namespace ProspectionCrm.Api.Services.Automation;

public sealed class AutomationEvaluationService(ProspectionCrmDbContext db, ICurrentWorkspaceProvider provider,
    IAutomationEventDispatcher dispatcher, IAutomationRuleEvaluator evaluator)
{
    public async Task<AutomationJobResult<AutomationDispatchSummary>> DispatchAsync(DispatchAutomationEventRequest request, CancellationToken token)
    {
        var workspace = await WorkspaceAsync(token);
        if (workspace is null) return new(null, 409, "WorkspaceUnavailable");
        return await dispatcher.DispatchAsync(new(workspace.Value, request.TriggerTypeCode, request.EventKey,
            request.PipelineId, request.Payload.GetRawText()), token);
    }

    public async Task<AutomationJobResult<AutomationEvaluationResult>> PreviewAsync(Guid id, CancellationToken token)
    {
        var workspace = await WorkspaceAsync(token);
        if (workspace is null) return new(null, 409, "WorkspaceUnavailable");
        var job = await db.AutomationJobs.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.WorkspaceId == workspace, token);
        if (job is null) return new(null, 404, "ResourceNotFound");
        if (job.AutomationRuleId is null) return new(null, 409, AutomationEvaluationReasons.RuleRequired);
        var rule = await db.AutomationRules.AsNoTracking().SingleOrDefaultAsync(x => x.Id == job.AutomationRuleId && x.WorkspaceId == workspace, token);
        if (rule is null) return new(null, 404, "ResourceNotFound");
        var settings = await db.AutomationRuntimeSettings.AsNoTracking().SingleOrDefaultAsync(x => x.WorkspaceId == workspace, token);
        if (settings is null) return new(null, 409, "AutomationSettingsMissing");
        return new(evaluator.Evaluate(job, rule, settings));
    }

    private async Task<Guid?> WorkspaceAsync(CancellationToken token)
    {
        try { return await provider.GetCurrentWorkspaceIdAsync(token); }
        catch (InvalidOperationException) { return null; }
    }
}
