using Microsoft.EntityFrameworkCore;
using ProspectionCrm.Api.Data;
using ProspectionCrm.Api.Entities;

namespace ProspectionCrm.Api.Services.Automation;

// These transitions participate in the caller's transaction and workspace automation lock.
// Keeping them separate leaves the Phase 8.2 public queue capabilities unchanged.
public sealed class AutomationActionRequestStore(ProspectionCrmDbContext db, AutomationRuntimeStore runtime)
{
    public async Task AwaitApprovalAsync(AutomationJob job, string owner, AutomationRuntimeInput input, CancellationToken token)
    {
        RequireTransaction();
        await runtime.EnsureLeaseAsync(job, owner, token);
        if (input.ActionRequest is null)
        {
            var evaluation = input.Evaluation!;
            if (!NeedsDecision(input)) throw new InvalidOperationException("A matched human decision is required.");
            var fingerprint = AutomationActionSnapshot.Fingerprint(input.Rule!)
                ?? throw new InvalidOperationException("A valid rule definition is required.");
            var plan = AutomationActionSnapshot.Serialize(evaluation.ActionPlan!);
            var id = Guid.NewGuid();
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "AutomationActionRequests" ("Id","WorkspaceId","AutomationJobId","AutomationRuleId",
                    "DecisionRequirementCode","StatusCode","ActionTypeCode","ActionCategoryCode","ActionPlanJson",
                    "RuleFingerprint","RequestedReasonCode","RequestedAt")
                VALUES ({id},{job.WorkspaceId},{job.Id},{job.AutomationRuleId},
                    {evaluation.SafetyDecision!.DecisionCode},{ActionRequestStatuses.Pending},{evaluation.ActionTypeCode},
                    {evaluation.ActionCategoryCode},CAST({plan} AS jsonb),{fingerprint},{evaluation.ReasonCode},{runtime.Now})
                ON CONFLICT ("AutomationJobId") DO NOTHING
                """, token);
        }
        // Recheck the lease in the statement that relinquishes it, after all preceding waits.
        var changed = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "AutomationJobs" j SET "StatusCode" = {AutomationJobStatuses.AwaitingApproval},
                "LeaseOwner" = NULL, "LeaseExpiresAt" = NULL, "CompletedAt" = NULL,
                "LastError" = NULL, "UpdatedAt" = clock_timestamp()
            WHERE j."Id" = {job.Id} AND j."WorkspaceId" = {job.WorkspaceId}
                AND j."StatusCode" = {AutomationJobStatuses.Leased} AND j."LeaseOwner" = {owner}
                AND j."AttemptCount" = {job.AttemptCount} AND j."LeaseExpiresAt" > clock_timestamp()
                AND EXISTS (SELECT 1 FROM "AutomationActionRequests" r WHERE r."AutomationJobId" = j."Id"
                    AND r."WorkspaceId" = j."WorkspaceId" AND r."AutomationRuleId" = j."AutomationRuleId"
                    AND r."StatusCode" = {ActionRequestStatuses.Pending})
            """, token);
        if (changed != 1) throw new AutomationLeaseLostException();
    }

    public Task<bool> ResumeApprovedAsync(AutomationActionRequest request, CancellationToken token)
        => TransitionAsync(request, true, token);
    public Task<bool> RejectAwaitingApprovalAsync(AutomationActionRequest request, CancellationToken token)
        => TransitionAsync(request, false, token);

    private async Task<bool> TransitionAsync(AutomationActionRequest request, bool approve, CancellationToken token)
    {
        RequireTransaction();
        var status = approve ? AutomationJobStatuses.Pending : AutomationJobStatuses.Cancelled;
        var decision = approve ? ActionRequestStatuses.Approved : ActionRequestStatuses.Rejected;
        return await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "AutomationJobs" j SET "StatusCode" = {status}, "UpdatedAt" = clock_timestamp(),
                "AvailableAt" = CASE WHEN {approve} THEN clock_timestamp() ELSE j."AvailableAt" END,
                "CompletedAt" = CASE WHEN {approve} THEN NULL ELSE clock_timestamp() END,
                "LeaseOwner" = NULL, "LeaseExpiresAt" = NULL, "LastError" = NULL
            WHERE j."Id" = {request.AutomationJobId} AND j."WorkspaceId" = {request.WorkspaceId}
                AND j."StatusCode" = {AutomationJobStatuses.AwaitingApproval}
                AND EXISTS (SELECT 1 FROM "AutomationActionRequests" r WHERE r."Id" = {request.Id}
                    AND r."AutomationJobId" = j."Id" AND r."WorkspaceId" = j."WorkspaceId" AND r."StatusCode" = {decision})
            """, token) == 1;
    }

    private void RequireTransaction()
    {
        if (db.Database.CurrentTransaction is null) throw new InvalidOperationException("A decision requires a transaction.");
    }

    public static bool NeedsDecision(AutomationRuntimeInput input) => input.ActionRequest is null
        && input.Evaluation is { IsValid: true, IsMatched: true, ActionPlan: not null } evaluation
        && ActionRequestCodes.RequiresDecision(evaluation.SafetyDecision?.DecisionCode);
}
