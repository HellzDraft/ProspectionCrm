using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ProspectionCrm.Api.Data;
using ProspectionCrm.Api.Dtos.EmailMessages;
using ProspectionCrm.Api.Entities;
using ProspectionCrm.Api.Security;
using ProspectionCrm.Api.Services.Automation;

namespace ProspectionCrm.Api.Services.Email;

public sealed class EmailSendPlanService(ProspectionCrmDbContext db, EmailAccountService accounts,
    IAutomationSafetyPolicy policy, IHumanActor actor)
{
    public async Task<EmailSendPlanDto?> GetAsync(Guid workspace, Guid id, CancellationToken token) =>
        await db.EmailSendPlans.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.WorkspaceId == workspace, token) is { } p ? Dto(p) : null;

    public async Task<List<EmailSendPlanDto>> ListAsync(Guid workspace, int offset, int limit, CancellationToken token)
    {
        if (offset < 0 || limit is < 1 or > 200) throw new EmailSyncException("EmailSendPaginationInvalid", 400);
        return (await db.EmailSendPlans.AsNoTracking().Where(x => x.WorkspaceId == workspace)
            .OrderByDescending(x => x.CreatedAt).ThenBy(x => x.Id).Skip(offset).Take(limit).ToListAsync(token)).Select(Dto).ToList();
    }
    public async Task<EmailSendPlanDto> CreateAsync(Guid workspace, EmailSendContent content, CancellationToken token)
    {
        await RequireHuman(workspace, token);
        content = EmailSendSnapshots.Normalize(content);
        await using var tx = await db.Database.BeginTransactionAsync(token);
        await LockWorkspace(workspace, token);
        var plan = await CreateLocked(workspace, content, token);
        await db.SaveChangesAsync(token); await tx.CommitAsync(token); return Dto(plan);
    }
    public async Task<EmailSendPlanDto> UpdateAsync(Guid workspace, Guid id, UpdateEmailSendPlanRequest body, CancellationToken token)
    {
        await RequireHuman(workspace, token);
        if (body.Content is null) throw new EmailSyncException("EmailSendContentInvalid", 400);
        var content = EmailSendSnapshots.Normalize(body.Content);
        await using var tx = await db.Database.BeginTransactionAsync(token);
        await LockWorkspace(workspace, token);
        var plan = await LockPlan(workspace, id, token);
        RequireRevision(plan, body.Revision);
        if (plan.StatusCode != "draft") throw new EmailSyncException("EmailSendPlanFrozen");
        var account = await Account(workspace, content.EmailAccountId, null, null, token);
        await References(workspace, content, token);
        Apply(plan, content, account);
        plan.Revision++; plan.UpdatedAt = await Now(token);
        plan.ContentHash = EmailSendSnapshots.Hash(EmailSendSnapshots.From(plan));
        await db.SaveChangesAsync(token); await tx.CommitAsync(token); return Dto(plan);
    }
    public async Task<EmailSendPlanDto> SubmitAsync(Guid workspace, Guid id, long revision, CancellationToken token)
    {
        await RequireHuman(workspace, token);
        await using var tx = await db.Database.BeginTransactionAsync(token);
        await LockWorkspace(workspace, token);
        var plan = await LockPlan(workspace, id, token);
        // A repeated submit is a controlled conflict; old revisions never select a new snapshot.
        RequireRevision(plan, revision);
        if (plan.StatusCode != "draft") throw new EmailSyncException("EmailSendPlanFrozen");
        var decision = await Decision(workspace, token);
        await SubmitLocked(plan, decision, null, null, token);
        await db.SaveChangesAsync(token); await tx.CommitAsync(token); return Dto(plan);
    }
    public async Task<EmailSendPlanDto> CancelAsync(Guid workspace, Guid id, long revision, CancellationToken token)
    {
        var user = await RequireHuman(workspace, token);
        await using var tx = await db.Database.BeginTransactionAsync(token);
        await LockWorkspace(workspace, token);
        // Common advisory lock serializes submit/update/cancel/decisions; request precedes plan.
        var requestId = await db.EmailSendPlans.AsNoTracking().Where(x => x.WorkspaceId == workspace && x.Id == id)
            .Select(x => x.AutomationActionRequestId).SingleOrDefaultAsync(token);
        AutomationActionRequest? request = requestId is { } rid ? await db.AutomationActionRequests.FromSqlInterpolated($"""
            SELECT * FROM "AutomationActionRequests" WHERE "Id" = {rid} AND "WorkspaceId" = {workspace} FOR UPDATE
            """).SingleAsync(token) : null;
        var plan = await LockPlan(workspace, id, token);
        RequireRevision(plan, revision);
        if (plan.StatusCode is not ("draft" or "pending-approval")) throw new EmailSyncException("EmailSendPlanTerminal");
        var now = await Now(token);
        if (request is not null)
        {
            if (request.StatusCode != "pending") throw new EmailSyncException("EmailSendDecisionConflict");
            request.StatusCode = "cancelled"; request.DecidedAt = now; request.UpdatedAt = now; request.DecidedByUserId = user;
            await FinishPreparationJob(request, false, token);
        }
        plan.StatusCode = "cancelled"; plan.CancelledAt = now; plan.UpdatedAt = now; plan.Revision++;
        if (plan.SubmittedRevision is null) plan.ContentHash = EmailSendSnapshots.Hash(EmailSendSnapshots.From(plan));
        await db.SaveChangesAsync(token); await tx.CommitAsync(token); return Dto(plan);
    }

    // Called only by the existing approval service inside its human-authorized transaction.
    internal async Task DecideLocked(AutomationActionRequest request, bool approve, CancellationToken token)
    {
        RequireTransaction();
        var plan = await db.EmailSendPlans.FromSqlInterpolated($"""
            SELECT * FROM "EmailSendPlans" WHERE "AutomationActionRequestId" = {request.Id}
                AND "WorkspaceId" = {request.WorkspaceId} FOR UPDATE
            """).SingleOrDefaultAsync(token) ?? throw new EmailSyncException("EmailSendPlanNotFound", 404);
        if (plan.StatusCode != "pending-approval" || request.StatusCode != "pending") throw new EmailSyncException("EmailSendDecisionConflict");
        if (approve)
        {
            var snapshot = EmailSendSnapshots.Parse(request.ActionPlanJson);
            if (!EmailSendSnapshots.ValidRequest(request) || snapshot is null || snapshot.PlanId != plan.Id
                || request.ContentFingerprint != plan.ContentHash
                || EmailSendSnapshots.Hash(EmailSendSnapshots.From(plan)) != plan.ContentHash
                || EmailSendSnapshots.Serialize(snapshot) != EmailSendSnapshots.Serialize(EmailSendSnapshots.From(plan)))
                throw new EmailSyncException("EmailSendSnapshotChanged");
            await Account(plan.WorkspaceId, plan.EmailAccountId, plan.FromAddressSnapshot, plan.ConnectionId, token);
            await References(plan.WorkspaceId, Content(plan), token);
            if (request.AutomationRuleId is { } ruleId)
            {
                var rule = await db.AutomationRules.FromSqlInterpolated($"""
                    SELECT * FROM "AutomationRules" WHERE "Id" = {ruleId} AND "WorkspaceId" = {plan.WorkspaceId} FOR SHARE
                    """).AsNoTracking().SingleOrDefaultAsync(token);
                if (rule is null || !rule.Enabled || rule.ArchivedAt is not null || rule.ActionTypeCode != EmailSendSnapshots.ActionType
                    || AutomationActionSnapshot.Fingerprint(rule) != request.RuleFingerprint) throw new EmailSyncException("EmailSendRuleChanged");
            }
        }
        var now = await Now(token);
        plan.StatusCode = approve ? "approved" : "rejected";
        plan.ApprovedAt = approve ? now : null; plan.RejectedAt = approve ? null : now;
        plan.UpdatedAt = now; plan.Revision++;
        await FinishPreparationJob(request, approve, token);
    }

    // The worker already holds its workspace lock, rule/settings locks and valid job lease.
    internal async Task PrepareAutomationLocked(AutomationJob job, AutomationRuntimeInput input, CancellationToken token)
    {
        RequireTransaction();
        var evaluation = input.Evaluation!;
        if (evaluation.EmailPlan is null || evaluation.ActionTypeCode != EmailSendSnapshots.ActionType
            || evaluation.SafetyDecision is not { } decision || !ActionRequestCodes.RequiresDecision(decision.DecisionCode))
            throw new EmailSyncException("EmailSendApprovalRequired");
        await EmailSyncLocks.WorkspaceAsync(db, job.WorkspaceId, token);
        var plan = await CreateLocked(job.WorkspaceId, evaluation.EmailPlan, token);
        await SubmitLocked(plan, decision, job, input.Rule, token);
        await db.SaveChangesAsync(token);
    }
    private async Task<EmailSendPlan> CreateLocked(Guid workspace, EmailSendContent content, CancellationToken token)
    {
        content = EmailSendSnapshots.Normalize(content);
        var account = await Account(workspace, content.EmailAccountId, null, null, token);
        await References(workspace, content, token);
        var plan = new EmailSendPlan { WorkspaceId = workspace, CreatedAt = await Now(token),
            FromAddressSnapshot = "", ToAddressesJson = "[]", CcAddressesJson = "[]", BodyText = "", ContentHash = "" };
        Apply(plan, content, account); plan.ContentHash = EmailSendSnapshots.Hash(EmailSendSnapshots.From(plan));
        db.EmailSendPlans.Add(plan); return plan;
    }
    private async Task SubmitLocked(EmailSendPlan plan, AutomationSafetyDecision decision, AutomationJob? job, AutomationRule? rule, CancellationToken token)
    {
        await Account(plan.WorkspaceId, plan.EmailAccountId, plan.FromAddressSnapshot, plan.ConnectionId, token);
        await References(plan.WorkspaceId, Content(plan), token);
        var snapshot = EmailSendSnapshots.From(plan);
        if (EmailSendSnapshots.Hash(snapshot) != plan.ContentHash) throw new EmailSyncException("EmailSendSnapshotChanged");
        var now = await Now(token);
        var request = new AutomationActionRequest { WorkspaceId = plan.WorkspaceId, AutomationJobId = job?.Id, AutomationRuleId = rule?.Id,
            DecisionRequirementCode = decision.DecisionCode, ActionTypeCode = EmailSendSnapshots.ActionType,
            ActionCategoryCode = AutomationCategories.Email, ActionPlanJson = EmailSendSnapshots.Serialize(snapshot),
            ContentFingerprint = plan.ContentHash, RuleFingerprint = rule is null ? plan.ContentHash : AutomationActionSnapshot.Fingerprint(rule)!,
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
        var decision = policy.Evaluate(settings ?? new() { WorkspaceId = workspace }, AutomationCategories.Email);
        if (!ActionRequestCodes.RequiresDecision(decision.DecisionCode)) throw new EmailSyncException("EmailSendPreparationDisabled");
        return decision;
    }
    private async Task<EmailAccount> Account(Guid workspace, Guid id, string? from, Guid? connection, CancellationToken token)
    {
        var account = await EmailSyncLocks.AccountAsync(db, workspace, id, token);
        if (account.StatusCode != "active" || account.ArchivedAt is not null) throw new EmailSyncException("EmailAccountUnavailable");
        accounts.RequireConnection(workspace, account.ConnectionId);
        if (from is not null && from != account.EmailAddress || connection is not null && connection != account.ConnectionId)
            throw new EmailSyncException("EmailSendAccountChanged");
        return account;
    }
    private async Task References(Guid workspace, EmailSendContent content, CancellationToken token)
    {
        if (content.OpportunityId is { } opportunity && !await db.Opportunities.FromSqlInterpolated($"""
            SELECT * FROM "Opportunities" WHERE "WorkspaceId" = {workspace} AND "Id" = {opportunity} FOR SHARE
            """).AnyAsync(token)) throw new EmailSyncException("EmailSendReferenceNotFound", 404);
        if (content.CompanyId is { } company && !await db.Companies.FromSqlInterpolated($"""
            SELECT * FROM "Companies" WHERE "WorkspaceId" = {workspace} AND "Id" = {company} FOR SHARE
            """).AnyAsync(token)) throw new EmailSyncException("EmailSendReferenceNotFound", 404);
        if (content.ContactId is { } contact && !await db.Contacts.FromSqlInterpolated($"""
            SELECT * FROM "Contacts" WHERE "WorkspaceId" = {workspace} AND "Id" = {contact} FOR SHARE
            """).AnyAsync(token)) throw new EmailSyncException("EmailSendReferenceNotFound", 404);
    }
    private async Task FinishPreparationJob(AutomationActionRequest request, bool approved, CancellationToken token)
    {
        if (request.AutomationJobId is not { } jobId) return;
        var status = approved ? AutomationJobStatuses.Completed : AutomationJobStatuses.Cancelled;
        var changed = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "AutomationJobs" SET "StatusCode" = {status}, "CompletedAt" = clock_timestamp(),
                "UpdatedAt" = clock_timestamp(), "LeaseOwner" = NULL, "LeaseExpiresAt" = NULL
            WHERE "Id" = {jobId} AND "WorkspaceId" = {request.WorkspaceId} AND "AutomationRuleId" = {request.AutomationRuleId}
                AND "ActionCategoryCode" = 'email' AND "StatusCode" = 'awaiting-approval'
            """, token);
        if (changed != 1) throw new EmailSyncException("EmailSendJobStateConflict");
    }
    private async Task<Guid> RequireHuman(Guid workspace, CancellationToken token)
    {
        var identity = await actor.RequireAsync(token);
        if (identity.WorkspaceId != workspace) throw new UnauthorizedAccessException();
        return identity.UserId;
    }
    private async Task LockWorkspace(Guid workspace, CancellationToken token)
    { await EmailSyncLocks.WorkspaceAsync(db, workspace, token); await AutomationRuntimeLock.AcquireAsync(db, workspace, token); }
    private async Task<EmailSendPlan> LockPlan(Guid workspace, Guid id, CancellationToken token) =>
        await db.EmailSendPlans.FromSqlInterpolated($"""
            SELECT * FROM "EmailSendPlans" WHERE "Id" = {id} AND "WorkspaceId" = {workspace} FOR UPDATE
            """).SingleOrDefaultAsync(token) ?? throw new EmailSyncException("EmailSendPlanNotFound", 404);
    private void RequireTransaction() { if (db.Database.CurrentTransaction is null) throw new InvalidOperationException("Email preparation requires a transaction."); }
    private Task<DateTimeOffset> Now(CancellationToken token) => EmailSyncLocks.Now(db, token);
    private static void RequireRevision(EmailSendPlan plan, long revision)
    { if (revision < 0 || plan.Revision != revision) throw new EmailSyncException("EmailSendRevisionConflict"); }
    private static EmailSendContent Content(EmailSendPlan plan) => new(plan.EmailAccountId,
        JsonSerializer.Deserialize<string[]>(plan.ToAddressesJson)!, JsonSerializer.Deserialize<string[]>(plan.CcAddressesJson)!,
        plan.Subject, plan.BodyText, plan.OpportunityId, plan.CompanyId, plan.ContactId);
    private static void Apply(EmailSendPlan plan, EmailSendContent content, EmailAccount account)
    {
        plan.EmailAccountId = account.Id; plan.ConnectionId = account.ConnectionId; plan.FromAddressSnapshot = account.EmailAddress;
        plan.ToAddressesJson = JsonSerializer.Serialize(content.ToAddresses); plan.CcAddressesJson = JsonSerializer.Serialize(content.CcAddresses);
        plan.Subject = content.Subject; plan.BodyText = content.BodyText;
        plan.OpportunityId = content.OpportunityId; plan.CompanyId = content.CompanyId; plan.ContactId = content.ContactId;
    }
    private static EmailSendPlanDto Dto(EmailSendPlan p) => new(p.Id, p.StatusCode, p.Revision, p.SubmittedRevision,
        p.AutomationActionRequestId, p.ContentHash, EmailSendSnapshots.From(p), p.CreatedAt, p.UpdatedAt,
        p.SubmittedAt, p.ApprovedAt, p.RejectedAt, p.CancelledAt);
}
