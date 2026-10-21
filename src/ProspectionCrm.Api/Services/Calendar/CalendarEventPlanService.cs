using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ProspectionCrm.Api.Data;
using ProspectionCrm.Api.Dtos.CalendarEvents;
using ProspectionCrm.Api.Entities;
using ProspectionCrm.Api.Security;
using ProspectionCrm.Api.Services.Automation;

namespace ProspectionCrm.Api.Services.Calendar;

public sealed class CalendarEventPlanService(ProspectionCrmDbContext db, CalendarAccountService accounts,
    IAutomationSafetyPolicy policy, IHumanActor actor)
{
    public async Task<CalendarEventPlanDto?> GetAsync(Guid workspace, Guid id, CancellationToken token) =>
        await db.CalendarEventPlans.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.WorkspaceId == workspace, token) is { } p ? Dto(p) : null;

    public async Task<List<CalendarEventPlanDto>> ListAsync(Guid workspace, int offset, int limit, CancellationToken token)
    {
        if (offset < 0 || limit is < 1 or > 200) throw new CalendarSyncException("CalendarEventPaginationInvalid", 400);
        return (await db.CalendarEventPlans.AsNoTracking().Where(x => x.WorkspaceId == workspace)
            .OrderByDescending(x => x.CreatedAt).ThenBy(x => x.Id).Skip(offset).Take(limit).ToListAsync(token)).Select(Dto).ToList();
    }
    public async Task<CalendarEventPlanDto> CreateAsync(Guid workspace, CalendarEventContent content, CancellationToken token)
    {
        await RequireHuman(workspace, token);
        content = CalendarEventSnapshots.Normalize(content);
        await using var tx = await db.Database.BeginTransactionAsync(token);
        await LockWorkspace(workspace, token);
        var plan = await CreateLocked(workspace, content, token);
        await db.SaveChangesAsync(token); await tx.CommitAsync(token); return Dto(plan);
    }
    public async Task<CalendarEventPlanDto> UpdateAsync(Guid workspace, Guid id, UpdateCalendarEventPlanRequest body, CancellationToken token)
    {
        await RequireHuman(workspace, token);
        if (body.Content is null) throw new CalendarSyncException("CalendarEventContentInvalid", 400);
        var content = CalendarEventSnapshots.Normalize(body.Content);
        await using var tx = await db.Database.BeginTransactionAsync(token);
        await LockWorkspace(workspace, token);
        var plan = await LockPlan(workspace, id, token);
        RequireRevision(plan, body.Revision);
        if (plan.StatusCode != "draft") throw new CalendarSyncException("CalendarEventPlanFrozen");
        var (account, calendar) = await Calendar(workspace, content.CalendarId, null, null, token);
        await References(workspace, content, token);
        await Apply(plan, content, account, calendar, token);
        plan.Revision++; plan.UpdatedAt = await Now(token);
        plan.ContentHash = CalendarEventSnapshots.Hash(CalendarEventSnapshots.From(plan));
        await db.SaveChangesAsync(token); await tx.CommitAsync(token); return Dto(plan);
    }
    public async Task<CalendarEventPlanDto> SubmitAsync(Guid workspace, Guid id, long revision, CancellationToken token)
    {
        await RequireHuman(workspace, token);
        await using var tx = await db.Database.BeginTransactionAsync(token);
        await LockWorkspace(workspace, token);
        var plan = await LockPlan(workspace, id, token);
        // A repeated submit is a controlled conflict; old revisions never select a new snapshot.
        RequireRevision(plan, revision);
        if (plan.StatusCode != "draft") throw new CalendarSyncException("CalendarEventPlanFrozen");
        var decision = await Decision(workspace, token);
        await SubmitLocked(plan, decision, token);
        await db.SaveChangesAsync(token); await tx.CommitAsync(token); return Dto(plan);
    }
    public async Task<CalendarEventPlanDto> CancelAsync(Guid workspace, Guid id, long revision, CancellationToken token)
    {
        var user = await RequireHuman(workspace, token);
        await using var tx = await db.Database.BeginTransactionAsync(token);
        await LockWorkspace(workspace, token);
        // Common advisory lock serializes submit/update/cancel/decisions; request precedes plan.
        var requestId = await db.CalendarEventPlans.AsNoTracking().Where(x => x.WorkspaceId == workspace && x.Id == id)
            .Select(x => x.AutomationActionRequestId).SingleOrDefaultAsync(token);
        AutomationActionRequest? request = requestId is { } rid ? await db.AutomationActionRequests.FromSqlInterpolated($"""
            SELECT * FROM "AutomationActionRequests" WHERE "Id" = {rid} AND "WorkspaceId" = {workspace} FOR UPDATE
            """).SingleAsync(token) : null;
        var plan = await LockPlan(workspace, id, token);
        RequireRevision(plan, revision);
        if (plan.StatusCode is not ("draft" or "pending-approval")) throw new CalendarSyncException("CalendarEventPlanTerminal");
        var now = await Now(token);
        if (request is not null)
        {
            if (request.StatusCode != "pending") throw new CalendarSyncException("CalendarEventDecisionConflict");
            request.StatusCode = "cancelled"; request.DecidedAt = now; request.UpdatedAt = now; request.DecidedByUserId = user;
        }
        plan.StatusCode = "cancelled"; plan.CancelledAt = now; plan.UpdatedAt = now; plan.Revision++;
        if (plan.SubmittedRevision is null) plan.ContentHash = CalendarEventSnapshots.Hash(CalendarEventSnapshots.From(plan));
        await db.SaveChangesAsync(token); await tx.CommitAsync(token); return Dto(plan);
    }

    // Called only by the existing approval service inside its human-authorized transaction.
    internal async Task DecideLocked(AutomationActionRequest request, bool approve, CancellationToken token)
    {
        RequireTransaction();
        var plan = await db.CalendarEventPlans.FromSqlInterpolated($"""
            SELECT * FROM "CalendarEventPlans" WHERE "AutomationActionRequestId" = {request.Id}
                AND "WorkspaceId" = {request.WorkspaceId} FOR UPDATE
            """).SingleOrDefaultAsync(token) ?? throw new CalendarSyncException("CalendarEventPlanNotFound", 404);
        if (plan.StatusCode != "pending-approval" || request.StatusCode != "pending") throw new CalendarSyncException("CalendarEventDecisionConflict");
        if (approve)
        {
            var snapshot = CalendarEventSnapshots.Parse(request.ActionPlanJson);
            if (!CalendarEventSnapshots.ValidRequest(request) || snapshot is null || snapshot.PlanId != plan.Id
                || request.ContentFingerprint != plan.ContentHash
                || CalendarEventSnapshots.Hash(CalendarEventSnapshots.From(plan)) != plan.ContentHash
                || CalendarEventSnapshots.Serialize(snapshot) != CalendarEventSnapshots.Serialize(CalendarEventSnapshots.From(plan)))
                throw new CalendarSyncException("CalendarEventSnapshotChanged");
            await Revalidate(plan, token);
        }
        var now = await Now(token);
        plan.StatusCode = approve ? "approved" : "rejected";
        plan.ApprovedAt = approve ? now : null; plan.RejectedAt = approve ? null : now;
        plan.UpdatedAt = now; plan.Revision++;
    }

    private async Task<CalendarEventPlan> CreateLocked(Guid workspace, CalendarEventContent content, CancellationToken token)
    {
        content = CalendarEventSnapshots.Normalize(content);
        var (account, calendar) = await Calendar(workspace, content.CalendarId, null, null, token);
        await References(workspace, content, token);
        var plan = new CalendarEventPlan { WorkspaceId = workspace, CreatedAt = await Now(token),
            OperationCode = content.OperationCode, ContentJson = "{}", ContentHash = "" };
        await Apply(plan, content, account, calendar, token); plan.ContentHash = CalendarEventSnapshots.Hash(CalendarEventSnapshots.From(plan));
        db.CalendarEventPlans.Add(plan); return plan;
    }
    private async Task SubmitLocked(CalendarEventPlan plan, AutomationSafetyDecision decision, CancellationToken token)
    {
        await Revalidate(plan, token);
        var snapshot = CalendarEventSnapshots.From(plan);
        if (CalendarEventSnapshots.Hash(snapshot) != plan.ContentHash) throw new CalendarSyncException("CalendarEventSnapshotChanged");
        var now = await Now(token);
        var request = new AutomationActionRequest { WorkspaceId = plan.WorkspaceId, AutomationJobId = null, AutomationRuleId = null,
            DecisionRequirementCode = decision.DecisionCode, ActionTypeCode = plan.OperationCode == "create" ? CalendarEventSnapshots.CreateAction : CalendarEventSnapshots.UpdateAction,
            ActionCategoryCode = AutomationCategories.Calendar, ActionPlanJson = CalendarEventSnapshots.Serialize(snapshot),
            ContentFingerprint = plan.ContentHash, RuleFingerprint = plan.ContentHash,
            RequestedReasonCode = decision.ReasonCode, RequestedAt = now };
        db.AutomationActionRequests.Add(request);
        plan.AutomationActionRequestId = request.Id; plan.SubmittedRevision = plan.Revision; plan.Revision++;
        plan.StatusCode = "pending-approval"; plan.SubmittedAt = now; plan.UpdatedAt = now;
    }
    private async Task<AutomationSafetyDecision> Decision(Guid workspace, CancellationToken token)
    {
        var settings = await db.AutomationRuntimeSettings.FromSqlInterpolated($"""
            SELECT * FROM "AutomationRuntimeSettings" WHERE "WorkspaceId" = {workspace} FOR SHARE
            """).AsNoTracking().SingleOrDefaultAsync(token);
        var decision = policy.Evaluate(settings ?? new() { WorkspaceId = workspace }, AutomationCategories.Calendar);
        if (!ActionRequestCodes.RequiresDecision(decision.DecisionCode)) throw new CalendarSyncException("CalendarEventPreparationDisabled");
        return decision;
    }
    private async Task<(CalendarAccount, Entities.Calendar)> Calendar(Guid workspace, Guid id, Guid? expectedAccount, Guid? connection, CancellationToken token)
    {
        var (account, calendar) = await CalendarSyncLocks.CalendarAsync(db, workspace, id, token);
        if (account.StatusCode != "active" || account.ArchivedAt is not null || calendar.StatusCode != "active" || calendar.ArchivedAt is not null)
            throw new CalendarSyncException("CalendarPlanCalendarUnavailable");
        accounts.RequireConnection(workspace, account.ConnectionId);
        if (expectedAccount is not null && expectedAccount != account.Id || connection is not null && connection != account.ConnectionId)
            throw new CalendarSyncException("CalendarPlanCalendarChanged");
        return (account, calendar);
    }
    private async Task<CalendarEventTarget?> Target(Guid workspace, CalendarEventContent content, CancellationToken token)
    {
        if (content.TargetCalendarEventId is not { } id) return null;
        var e = await db.CalendarEvents.FromSqlInterpolated($"""
            SELECT * FROM "CalendarEvents" WHERE "Id" = {id} AND "WorkspaceId" = {workspace} FOR UPDATE
            """).AsNoTracking().SingleOrDefaultAsync(token);
        if (e is null || e.CalendarId != content.CalendarId) throw new CalendarSyncException("CalendarPlanTargetNotFound", 404);
        if (e.StatusCode != "confirmed" || e.ExternalRevision is null || string.IsNullOrEmpty(e.ExternalEventId)
            || e.ExternalContentSha256 is null || (e.SeriesId is null) != (e.OccurrenceId is null))
            throw new CalendarSyncException("CalendarPlanTargetUnavailable");
        return CalendarEventSnapshots.Target(e);
    }
    private async Task Revalidate(CalendarEventPlan plan, CancellationToken token)
    {
        await Calendar(plan.WorkspaceId, plan.CalendarId, plan.CalendarAccountId, plan.ConnectionId, token);
        var content = Content(plan);
        if (content.CalendarId != plan.CalendarId || content.OperationCode != plan.OperationCode
            || content.TargetCalendarEventId != plan.TargetCalendarEventId || content.OpportunityId != plan.OpportunityId
            || content.CompanyId != plan.CompanyId || content.ContactId != plan.ContactId)
            throw new CalendarSyncException("CalendarPlanSnapshotChanged");
        await References(plan.WorkspaceId, content, token);
        var target = await Target(plan.WorkspaceId, content, token);
        if (target != CalendarEventSnapshots.From(plan).Target || target?.ExternalEventId != plan.TargetExternalEventId
            || target?.Revision != plan.TargetExternalRevision) throw new CalendarSyncException("CalendarPlanTargetChanged");
    }
    private async Task References(Guid workspace, CalendarEventContent content, CancellationToken token)
    {
        if (content.OpportunityId is { } opportunity && !await db.Opportunities.FromSqlInterpolated($"""
            SELECT * FROM "Opportunities" WHERE "WorkspaceId" = {workspace} AND "Id" = {opportunity} FOR SHARE
            """).AnyAsync(token)) throw new CalendarSyncException("CalendarEventReferenceNotFound", 404);
        if (content.CompanyId is { } company && !await db.Companies.FromSqlInterpolated($"""
            SELECT * FROM "Companies" WHERE "WorkspaceId" = {workspace} AND "Id" = {company} FOR SHARE
            """).AnyAsync(token)) throw new CalendarSyncException("CalendarEventReferenceNotFound", 404);
        if (content.ContactId is { } contact && !await db.Contacts.FromSqlInterpolated($"""
            SELECT * FROM "Contacts" WHERE "WorkspaceId" = {workspace} AND "Id" = {contact} FOR SHARE
            """).AnyAsync(token)) throw new CalendarSyncException("CalendarEventReferenceNotFound", 404);
    }
    private async Task<Guid> RequireHuman(Guid workspace, CancellationToken token)
    {
        var identity = await actor.RequireAsync(token);
        if (identity.WorkspaceId != workspace) throw new UnauthorizedAccessException();
        return identity.UserId;
    }
    private async Task LockWorkspace(Guid workspace, CancellationToken token)
    { await CalendarSyncLocks.WorkspaceAsync(db, workspace, token);
        var identity = await actor.RequireAsync(token);
        if (!await db.Workspaces.AnyAsync(x => x.Id == workspace && x.OwnerUserId == identity.UserId, token)) throw new UnauthorizedAccessException();
        await AutomationRuntimeLock.AcquireAsync(db, workspace, token); }
    private async Task<CalendarEventPlan> LockPlan(Guid workspace, Guid id, CancellationToken token) =>
        await db.CalendarEventPlans.FromSqlInterpolated($"""
            SELECT * FROM "CalendarEventPlans" WHERE "Id" = {id} AND "WorkspaceId" = {workspace} FOR UPDATE
            """).SingleOrDefaultAsync(token) ?? throw new CalendarSyncException("CalendarEventPlanNotFound", 404);
    private void RequireTransaction() { if (db.Database.CurrentTransaction is null) throw new InvalidOperationException("Calendar preparation requires a transaction."); }
    private Task<DateTimeOffset> Now(CancellationToken token) => CalendarSyncLocks.Now(db, token);
    private static void RequireRevision(CalendarEventPlan plan, long revision)
    { if (revision < 0 || plan.Revision != revision) throw new CalendarSyncException("CalendarEventRevisionConflict"); }
    private static CalendarEventContent Content(CalendarEventPlan plan) =>
        JsonSerializer.Deserialize<CalendarEventContent>(plan.ContentJson, CalendarEventSnapshots.Json)!;
    private async Task Apply(CalendarEventPlan plan, CalendarEventContent content, CalendarAccount account, Entities.Calendar calendar, CancellationToken token)
    {
        var target = await Target(plan.WorkspaceId, content, token);
        plan.CalendarAccountId = account.Id; plan.ConnectionId = account.ConnectionId; plan.CalendarId = calendar.Id;
        plan.OperationCode = content.OperationCode; plan.TargetCalendarEventId = content.TargetCalendarEventId;
        plan.TargetExternalEventId = target?.ExternalEventId; plan.TargetExternalRevision = target?.Revision;
        plan.TargetSnapshotJson = target is null ? null : JsonSerializer.Serialize(target, CalendarEventSnapshots.Json);
        plan.ContentJson = JsonSerializer.Serialize(content, CalendarEventSnapshots.Json);
        plan.OpportunityId = content.OpportunityId; plan.CompanyId = content.CompanyId; plan.ContactId = content.ContactId;
    }
    private static CalendarEventPlanDto Dto(CalendarEventPlan p) => new(p.Id, p.StatusCode, p.Revision, p.SubmittedRevision,
        p.AutomationActionRequestId, p.ContentHash, CalendarEventSnapshots.From(p), p.CreatedAt, p.UpdatedAt,
        p.SubmittedAt, p.ApprovedAt, p.RejectedAt, p.CancelledAt);
}
