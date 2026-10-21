using Microsoft.EntityFrameworkCore;
using ProspectionCrm.Api.Data;
using ProspectionCrm.Api.Dtos.AutomationActionRequests;
using ProspectionCrm.Api.Entities;
using ProspectionCrm.Api.Security;
using ProspectionCrm.Api.Services.Email;

namespace ProspectionCrm.Api.Services.Automation;

public sealed class AutomationActionRequestService(ProspectionCrmDbContext db, ICurrentWorkspaceProvider workspaceProvider,
    AutomationRuntimeStore runtime, AutomationActionRequestStore store, IHumanActor humanActor, EmailSendPlanService? emails = null)
{
    public async Task<AutomationJobResult<AutomationActionRequestDto>> GetAsync(Guid id, CancellationToken token)
    {
        var workspace = await WorkspaceAsync(token);
        if (workspace is null) return new(null, 409, "WorkspaceUnavailable");
        var request = await ReadQuery(workspace.Value).SingleOrDefaultAsync(x => x.Id == id, token);
        return request is null ? new(null, 404, "ResourceNotFound") : new((await ToDtosAsync([request], token))[0]);
    }

    public async Task<AutomationJobResult<AutomationActionRequestsPageDto>> ListAsync(int offset, int limit,
        string? status, string? requirement, Guid? rule, Guid? job, string? action, CancellationToken token)
    {
        if (offset < 0 || limit is < 1 or > 200) return new(null, 400, "InvalidPagination");
        if (status is not null && !ActionRequestStatuses.IsValid(status)) return new(null, 400, "InvalidStatusCode");
        if (requirement is not null && !ActionRequestCodes.RequiresDecision(requirement)) return new(null, 400, "InvalidDecisionRequirement");
        if (action is not null && AutomationActionCatalog.Find(action) is null) return new(null, 400, "InvalidActionTypeCode");
        var workspace = await WorkspaceAsync(token);
        if (workspace is null) return new(null, 409, "WorkspaceUnavailable");
        var query = ReadQuery(workspace.Value).Where(x => (status == null || x.StatusCode == status)
            && (requirement == null || x.DecisionRequirementCode == requirement)
            && (rule == null || x.AutomationRuleId == rule) && (job == null || x.AutomationJobId == job)
            && (action == null || x.ActionTypeCode == action));
        var count = await query.CountAsync(token);
        var rows = await query.OrderByDescending(x => x.RequestedAt).ThenByDescending(x => x.Id)
            .Skip(offset).Take(limit).ToListAsync(token);
        return new(new(offset, limit, count, (long)offset + rows.Count < count, await ToDtosAsync(rows, token)));
    }

    public async Task<AutomationJobResult<AutomationActionRequestDto>> DecideAsync(Guid id,
        DecideAutomationActionRequest body, bool approve, CancellationToken token)
    {
        if (body.DecisionNote is { } note && (note.Length > ActionRequestCodes.NoteLength || !AutomationJson.SafeText(note)))
            return new(null, 400, "InvalidDecisionNote");
        var workspace = await WorkspaceAsync(token);
        if (workspace is null) return new(null, 409, "WorkspaceUnavailable");
        await using var tx = await db.Database.BeginTransactionAsync(token);
        var identity = await humanActor.RequireAsync(token);
        if (identity.WorkspaceId != workspace.Value) throw new UnauthorizedAccessException("operator-workspace-mismatch");
        var owner = await db.Workspaces.FromSqlInterpolated($"""
            SELECT * FROM "Workspaces" WHERE "Id" = {workspace.Value} FOR SHARE
            """).AsNoTracking().SingleOrDefaultAsync(token);
        if (owner?.OwnerUserId != identity.UserId || owner.ArchivedAt is not null)
            throw new UnauthorizedAccessException("operator-workspace-mismatch");
        await runtime.LockAsync(workspace.Value, token);
        var request = await db.AutomationActionRequests.FromSqlInterpolated($"""
            SELECT * FROM "AutomationActionRequests" WHERE "Id" = {id} AND "WorkspaceId" = {workspace.Value} FOR UPDATE
            """).SingleOrDefaultAsync(token);
        if (request is null) return new(null, 404, "ResourceNotFound");
        var desired = approve ? ActionRequestStatuses.Approved : ActionRequestStatuses.Rejected;
        if (request.StatusCode == desired)
        {
            await tx.CommitAsync(token);
            return await GetAsync(id, token);
        }
        if (request.StatusCode != ActionRequestStatuses.Pending) return new(null, 409, "ActionRequestAlreadyDecided");
        if (request.ActionTypeCode == EmailSendSnapshots.ActionType)
        {
            await (emails ?? throw new InvalidOperationException("Email preparation is unavailable.")).DecideLocked(request, approve, token);
            request.StatusCode = desired;
            request.DecidedAt = await EmailSyncLocks.Now(db, token); request.UpdatedAt = request.DecidedAt;
            request.DecidedByUserId = identity.UserId;
            request.DecisionNote = string.IsNullOrWhiteSpace(body.DecisionNote) ? null : body.DecisionNote;
            await db.SaveChangesAsync(token); await tx.CommitAsync(token); return await GetAsync(id, token);
        }
        var job = await db.AutomationJobs.FromSqlInterpolated($"""
            SELECT * FROM "AutomationJobs" WHERE "Id" = {request.AutomationJobId} AND "WorkspaceId" = {workspace.Value} FOR UPDATE
            """).AsNoTracking().SingleOrDefaultAsync(token);
        if (job?.StatusCode != AutomationJobStatuses.AwaitingApproval) return new(null, 409, "ActionRequestJobStateConflict");
        if (approve)
        {
            var rule = await db.AutomationRules.FromSqlInterpolated($"""
                SELECT * FROM "AutomationRules" WHERE "Id" = {request.AutomationRuleId} AND "WorkspaceId" = {workspace.Value} FOR SHARE
                """).AsNoTracking().SingleOrDefaultAsync(token);
            if (AutomationActionSnapshot.IsStale(request, rule)) return new(null, 409, "ActionRequestStale");
            if (!rule!.Enabled || rule.ArchivedAt is not null) return new(null, 409, "ActionRequestUnavailable");
            // Validate the exact snapshot without changing the persisted pending decision.
            if (job.AutomationRuleId != request.AutomationRuleId || request.ActionTypeCode != rule.ActionTypeCode
                || request.ActionTypeCode != AutomationActionCatalog.CreateCrmTask
                || request.ActionCategoryCode != AutomationCategories.General || request.ActionCategoryCode != job.ActionCategoryCode
                || AutomationActionSnapshot.Parse(request.ActionPlanJson) is null) return new(null, 409, "ActionRequestUnavailable");
        }
        request.StatusCode = desired;
        request.DecidedAt = runtime.Now < request.RequestedAt ? request.RequestedAt : runtime.Now;
        request.UpdatedAt = request.DecidedAt;
        request.DecidedByUserId = identity.UserId;
        request.DecisionNote = string.IsNullOrWhiteSpace(body.DecisionNote) ? null : body.DecisionNote;
        await db.SaveChangesAsync(token);
        var changed = approve ? await store.ResumeApprovedAsync(request, token) : await store.RejectAwaitingApprovalAsync(request, token);
        if (!changed) return new(null, 409, "ConcurrentActionRequestChange");
        await tx.CommitAsync(token);
        return await GetAsync(id, token);
    }

    private IQueryable<AutomationActionRequest> ReadQuery(Guid workspace) => db.AutomationActionRequests.AsNoTracking()
        .Where(x => x.WorkspaceId == workspace);
    private async Task<List<AutomationActionRequestDto>> ToDtosAsync(List<AutomationActionRequest> rows, CancellationToken token)
    {
        if (rows.Count == 0) return [];
        var workspace = rows[0].WorkspaceId;
        var ruleIds = rows.Select(x => x.AutomationRuleId).Distinct().ToArray();
        var jobIds = rows.Select(x => x.AutomationJobId).ToArray();
        var rules = await db.AutomationRules.AsNoTracking().Where(x => x.WorkspaceId == workspace && ruleIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, token);
        var jobs = await db.AutomationJobs.AsNoTracking().Where(x => x.WorkspaceId == workspace && jobIds.Contains(x.Id))
            .Select(x => new { x.Id, x.StatusCode }).ToDictionaryAsync(x => x.Id, x => x.StatusCode, token);
        // Do not let an INNER JOIN hide a request whose referenced rule is unexpectedly absent.
        return rows.Select(r => ToDto(r, r.AutomationRuleId is { } ruleId ? rules.GetValueOrDefault(ruleId) : null,
            r.AutomationJobId is { } jobId ? jobs.GetValueOrDefault(jobId) : null)).ToList();
    }
    private static AutomationActionRequestDto ToDto(AutomationActionRequest r, AutomationRule? rule, string? jobStatus) => new(r.Id, r.AutomationJobId,
        r.AutomationRuleId, rule?.Name, r.DecisionRequirementCode, r.StatusCode, r.ActionTypeCode,
        r.ActionCategoryCode, AutomationActionSnapshot.Parse(r.ActionPlanJson), r.RequestedReasonCode, r.RequestedAt,
        r.UpdatedAt, r.DecidedAt, r.DecidedByUserId, r.DecisionNote, jobStatus,
        AutomationActionSnapshot.IsStale(r, rule),
        r.ActionTypeCode == EmailSendSnapshots.ActionType && EmailSendSnapshots.ValidRequest(r) ? EmailSendSnapshots.Parse(r.ActionPlanJson) : null,
        r.ContentFingerprint);
    private async Task<Guid?> WorkspaceAsync(CancellationToken token)
    {
        try { return await workspaceProvider.GetCurrentWorkspaceIdAsync(token); }
        catch (InvalidOperationException) { return null; }
    }
}
