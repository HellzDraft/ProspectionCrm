using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ProspectionCrm.Api.Data;
using ProspectionCrm.Api.Dtos.Integrations;
using ProspectionCrm.Api.Entities;
using ProspectionCrm.Api.Security;
using ProspectionCrm.Api.Services.Automation;
using ProspectionCrm.Api.Services.Integrations;

namespace ProspectionCrm.Api.Services.Email;

public sealed class EmailSendDeliveryService(ProspectionCrmDbContext db, EmailAccountService accounts,
    IAutomationSafetyPolicy policy, IHumanActor human, IOptionsMonitor<IntegrationOptions> integrations,
    IOptionsMonitor<OperatorOptions> operators)
{
    public Task<EmailSendDelivery?> FindAsync(Guid workspace, Guid id, CancellationToken token = default) =>
        db.EmailSendDeliveries.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.WorkspaceId == workspace, token);

    public async Task<EmailSendDelivery> CreateAsync(Guid workspace, Guid planId, CancellationToken token = default)
    {
        await Human(workspace, token);
        await using var tx = await db.Database.BeginTransactionAsync(token);
        await Lock(workspace, token);
        var (plan, account) = await Authorize(workspace, planId, token);
        var existing = await db.EmailSendDeliveries.AsNoTracking().SingleOrDefaultAsync(x => x.EmailSendPlanId == planId, token);
        if (existing is not null) return existing;
        var now = await EmailSyncLocks.Now(db, token);
        var op = new IntegrationOperation { Id = Guid.NewGuid(), WorkspaceId = workspace, ConnectionId = plan.ConnectionId,
            CommandMessageId = Guid.NewGuid(), CorrelationId = Guid.NewGuid(), Version = 1, Type = "email.send", StatusCode = "pending", CreatedAt = now };
        var delivery = new EmailSendDelivery { WorkspaceId = workspace, EmailSendPlanId = plan.Id,
            AutomationActionRequestId = plan.AutomationActionRequestId!.Value, EmailAccountId = plan.EmailAccountId,
            ConnectionId = plan.ConnectionId, IntegrationOperationId = op.Id, ContentHash = plan.ContentHash,
            ProviderCode = account.ProviderCode, IdempotencyKey = "", CreatedAt = now };
        delivery.IdempotencyKey = $"email-send-{delivery.Id:D}";
        db.AddRange(op, delivery); await db.SaveChangesAsync(token); await tx.CommitAsync(token);
        db.Entry(op).State = EntityState.Detached; db.Entry(delivery).State = EntityState.Detached;
        return delivery;
    }

    internal async Task<(EmailSendCommand Command, Guid Attempt)> BeginAsync(Guid workspace, Guid id, CancellationToken token)
    {
        await Human(workspace, token);
        await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, token);
        await Lock(workspace, token);
        var initial = await FindAsync(workspace, id, token) ?? throw Error("EmailDeliveryNotFound", 404);
        var (plan, account) = await Authorize(workspace, initial.EmailSendPlanId, token);
        var (d, op) = await Locked(workspace, id, token);
        if (d.StatusCode != "pending" || op.StatusCode != "pending"
            || d.ContentHash != plan.ContentHash || d.AutomationActionRequestId != plan.AutomationActionRequestId
            || d.EmailAccountId != plan.EmailAccountId || d.ProviderCode != account.ProviderCode)
            throw Error("EmailDeliveryNotDispatchable");
        var limits = await db.AutomationRuntimeSettings.AsNoTracking().SingleAsync(x => x.WorkspaceId == workspace, token);
        var at = await EmailSyncLocks.Now(db, token);
        if (await db.EmailSendDeliveries.CountAsync(x => x.WorkspaceId == workspace && x.StartedAt >= at.AddMinutes(-1), token) >= limits.MaxExecutionsPerMinute
            || await db.EmailSendDeliveries.CountAsync(x => x.WorkspaceId == workspace && x.StartedAt >= at.AddDays(-1), token) >= limits.MaxExecutionsPerDay)
            throw Error("EmailSendRateLimited");
        var command = new EmailSendCommand(1, op.CommandMessageId, op.Id, op.CorrelationId, "email.send", d.Id,
            d.IdempotencyKey, EmailSendSnapshots.From(plan));
        _ = EmailSendContracts.Serialize(command);
        var (now, refusal) = await IntegrationDispatchBudget.CheckUnderRuntimeLockAsync(db, workspace, d.ConnectionId,
            integrations.CurrentValue.DispatchLimits, token);
        if (refusal is not null) throw Error(refusal);
        var attempt = Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "IntegrationAttempts" ("Id","OperationId","Number","StatusCode","StartedAt") VALUES ({attempt},{op.Id},1,'started',{now})
            """, token);
        d.StartedAt = now; d.StatusCode = "dispatching";
        await Persist(d, "awaiting-result", token); await tx.CommitAsync(token);
        return (command, attempt);
    }

    internal async Task CompleteAsync(Guid workspace, Guid id, Guid attempt, EmailSendTransportOutcome outcome, CancellationToken token)
    {
        await using var tx = await db.Database.BeginTransactionAsync(token); await Lock(workspace, token);
        var (d, op) = await Locked(workspace, id, token);
        var status = outcome == EmailSendTransportOutcome.Acknowledged ? "acknowledged" : outcome == EmailSendTransportOutcome.NotSent ? "failed" : "unknown";
        var changed = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "IntegrationAttempts" SET "StatusCode" = {status}, "FinishedAt" = GREATEST(statement_timestamp(), "StartedAt")
            WHERE "Id" = {attempt} AND "OperationId" = {op.Id} AND "StatusCode" = 'started'
            """, token);
        // A callback or reconciliation may have committed before this transport result.
        if (changed != 0 && d.StatusCode == "dispatching")
        {
            d.StatusCode = outcome switch { EmailSendTransportOutcome.Acknowledged => "awaiting-result", EmailSendTransportOutcome.NotSent => "failed", _ => "send-unknown" };
            d.DiagnosticCode = outcome == EmailSendTransportOutcome.NotSent ? "EmailDefinitelyNotSent" : outcome == EmailSendTransportOutcome.Unknown ? "EmailSendUnknown" : null;
            if (d.StatusCode == "failed") d.FinishedAt = await EmailSyncLocks.Now(db, token);
            await Persist(d, d.StatusCode == "failed" ? "failed" : "awaiting-result", token);
        }
        await tx.CommitAsync(token);
    }

    public async Task<IntegrationReception> ReceiveAsync(IntegrationAccess access, string json, CancellationToken token = default)
    {
        var result = EmailSendContracts.ParseResult(json); var canonical = EmailSendContracts.Serialize(result);
        var initial = await FindAsync(access.WorkspaceId, result.DeliveryId, token);
        if (initial is null || initial.ConnectionId != access.ConnectionId || access.ConnectionId == Guid.Empty) throw new IntegrationAccessException();
        await using var tx = await db.Database.BeginTransactionAsync(token); await Lock(access.WorkspaceId, token);
        var (d, op) = await Locked(access.WorkspaceId, initial.Id, token); Match(d, op, result);
        var prior = await db.IntegrationReceipts.AsNoTracking().SingleOrDefaultAsync(x => x.MessageId == result.MessageId, token);
        if (prior is not null)
        {
            if (prior.OperationId != op.Id || prior.CanonicalContent != canonical || prior.ContentSha256 != Hash(canonical)) throw Error("EmailEvidenceConflict");
            return IntegrationReception.AlreadyRecorded;
        }
        if (d.StartedAt is null) throw Error("EmailEvidenceBeforeDispatch");
        var number = (await db.IntegrationReceipts.Where(x => x.OperationId == op.Id).MaxAsync(x => (int?)x.EvidenceNumber, token) ?? 0) + 1;
        if (number > 100) throw Error("EmailEvidenceLimit");
        var now = await EmailSyncLocks.Now(db, token);
        var inserted = await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "IntegrationReceipts" ("MessageId","OperationId","EvidenceNumber","CanonicalContent","ContentSha256","ReceivedAt")
            VALUES ({result.MessageId},{op.Id},{number},{canonical},{Hash(canonical)},{now}) ON CONFLICT ("MessageId") DO NOTHING
            """, token);
        if (inserted != 1) throw Error("EmailEvidenceConflict");
        if (d.StatusCode is not ("sent" or "failed")) { d.StatusCode = "result-received"; await Persist(d, "result-received", token); }
        await tx.CommitAsync(token); return IntegrationReception.Recorded;
    }

    // Processes authenticated durable evidence only; never invokes a sending transport.
    public async Task ReconcileAsync(Guid workspace, Guid id, CancellationToken token = default)
    {
        await Human(workspace, token);
        await using var tx = await db.Database.BeginTransactionAsync(token); await Lock(workspace, token);
        var initial = await FindAsync(workspace, id, token) ?? throw Error("EmailDeliveryNotFound", 404);
        // Account precedes delivery/operation, including import's unique message identity.
        _ = await EmailSyncLocks.AccountAsync(db, workspace, initial.EmailAccountId, token);
        var (d, op) = await Locked(workspace, id, token);
        var receipts = await db.IntegrationReceipts.AsNoTracking().Where(x => x.OperationId == op.Id && x.ProcessedAt == null)
            .OrderBy(x => x.EvidenceNumber).ToListAsync(token);
        var now = await EmailSyncLocks.Now(db, token);
        foreach (var receipt in receipts)
        {
            var result = EmailSendContracts.ParseResult(receipt.CanonicalContent); Match(d, op, result);
            if (Hash(receipt.CanonicalContent) != receipt.ContentSha256 || result.MessageId != receipt.MessageId) throw Error("EmailEvidenceInvalid");
            if (result.Outcome == "accepted")
            {
                if (result.ProviderCode != d.ProviderCode || result.AcceptedAt < d.StartedAt || result.AcceptedAt > now.AddMinutes(5)) throw Error("EmailProviderProofInvalid");
                if (d.StatusCode == "failed" || d.StatusCode == "sent" && d.ExternalMessageId != result.ExternalMessageId) throw Error("EmailProviderProofConflict");
                if (d.StatusCode != "sent") await RecordMessage(d, result, now, token);
            }
            else if (d.StatusCode is not ("sent" or "failed"))
            {
                // Generic failed does not prove that the provider did not accept the message.
                d.StatusCode = result.Outcome == "not-sent" ? "failed" : "send-unknown";
                d.DiagnosticCode = result.Outcome == "not-sent" ? "EmailDefinitelyNotSent" : "EmailSendUnknown";
                if (d.StatusCode == "failed") d.FinishedAt = now;
            }
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE \"IntegrationReceipts\" SET \"ProcessedAt\" = {now} WHERE \"MessageId\" = {receipt.MessageId}", token);
        }
        if (receipts.Count == 0 && d.StatusCode is "dispatching" or "awaiting-result")
        {
            if (now - d.StartedAt < TimeSpan.FromMinutes(2)) throw Error("EmailAttemptNotRecoverable");
            d.StatusCode = "send-unknown"; d.DiagnosticCode = "EmailSendUnknown";
        }
        if (d.StatusCode == "send-unknown")
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE "IntegrationAttempts" SET "StatusCode" = 'unknown', "FinishedAt" = GREATEST(statement_timestamp(), "StartedAt")
                WHERE "OperationId" = {op.Id} AND "StatusCode" = 'started'
                """, token);
        await Persist(d, d.StatusCode switch { "sent" => "succeeded", "failed" => "failed", "pending" => "pending", _ => "awaiting-result" }, token);
        await tx.CommitAsync(token);
    }

    private async Task RecordMessage(EmailSendDelivery d, EmailSendResult proof, DateTimeOffset now, CancellationToken token)
    {
        var plan = await db.EmailSendPlans.AsNoTracking().SingleAsync(x => x.Id == d.EmailSendPlanId && x.WorkspaceId == d.WorkspaceId, token);
        if (plan.StatusCode != "approved" || EmailSendSnapshots.Hash(EmailSendSnapshots.From(plan)) != d.ContentHash) throw Error("EmailApprovedContentChanged");
        var existing = await db.EmailMessages.AsNoTracking().SingleOrDefaultAsync(x => x.WorkspaceId == d.WorkspaceId
            && x.EmailAccountId == d.EmailAccountId && x.ExternalMessageId == proof.ExternalMessageId, token);
        if (existing is not null) throw Error("EmailProviderIdentityConflict");
        var message = new EmailMessage { WorkspaceId = d.WorkspaceId, EmailAccountId = d.EmailAccountId, ProviderCode = d.ProviderCode,
            ExternalMessageId = proof.ExternalMessageId, DirectionCode = "outbound", FromAddress = plan.FromAddressSnapshot,
            ToAddressesJson = plan.ToAddressesJson, CcAddressesJson = plan.CcAddressesJson, Subject = plan.Subject, BodyText = plan.BodyText,
            OpportunityId = plan.OpportunityId, CompanyId = plan.CompanyId, ContactId = plan.ContactId,
            OccurredAt = proof.AcceptedAt!.Value, CreatedAt = now };
        db.EmailMessages.Add(message); await db.SaveChangesAsync(token); db.Entry(message).State = EntityState.Detached;
        d.StatusCode = "sent"; d.ExternalMessageId = proof.ExternalMessageId; d.EmailMessageId = message.Id; d.FinishedAt = now; d.DiagnosticCode = null;
    }

    private async Task<(EmailSendPlan, EmailAccount)> Authorize(Guid workspace, Guid planId, CancellationToken token)
    {
        var plan = await db.EmailSendPlans.AsNoTracking().SingleOrDefaultAsync(x => x.Id == planId && x.WorkspaceId == workspace, token)
            ?? throw Error("EmailSendPlanNotFound", 404);
        var request = await db.AutomationActionRequests.FromSqlInterpolated($"""
            SELECT * FROM "AutomationActionRequests" WHERE "Id" = {plan.AutomationActionRequestId} AND "WorkspaceId" = {workspace} FOR SHARE
            """).AsNoTracking().SingleOrDefaultAsync(token);
        if (plan.StatusCode != "approved" || request is null || request.StatusCode != "approved" || request.DecidedAt is null
            || !await db.Workspaces.AnyAsync(x => x.Id == workspace && x.OwnerUserId == request.DecidedByUserId
                && db.UserAccounts.Any(u => u.Id == x.OwnerUserId), token)
            || !EmailSendSnapshots.ValidRequest(request) || request.ContentFingerprint != plan.ContentHash
            || request.AutomationRuleId == null && request.RuleFingerprint != plan.ContentHash
            || EmailSendSnapshots.Hash(EmailSendSnapshots.From(plan)) != plan.ContentHash
            || EmailSendSnapshots.Serialize(EmailSendSnapshots.Parse(request.ActionPlanJson)!) != EmailSendSnapshots.Serialize(EmailSendSnapshots.From(plan)))
            throw Error("EmailApprovalInvalid");
        var config = integrations.CurrentValue;
        if (!config.Enabled || !config.IsValid(operators.CurrentValue)) throw Error("EmailSendDisabled");
        accounts.RequireConnection(workspace, plan.ConnectionId);
        var account = await EmailSyncLocks.AccountAsync(db, workspace, plan.EmailAccountId, token);
        if (account.StatusCode != "active" || account.ArchivedAt is not null || account.EmailAddress != plan.FromAddressSnapshot
            || account.ConnectionId != plan.ConnectionId) throw Error("EmailAccountChanged");
        var settings = await db.AutomationRuntimeSettings.FromSqlInterpolated($"""
            SELECT * FROM "AutomationRuntimeSettings" WHERE "WorkspaceId" = {workspace} FOR SHARE
            """).AsNoTracking().SingleOrDefaultAsync(token);
        if (settings is null || !ActionRequestCodes.RequiresDecision(policy.Evaluate(settings, "email").DecisionCode)) throw Error("EmailSendDisabled");
        if ((await new AutomationCircuitBreakerService(db, TimeProvider.System).ReadAsync(settings, token)).CanReset)
            throw Error("EmailCircuitOpen");
        if (request.AutomationRuleId is { } ruleId)
        {
            var rule = await db.AutomationRules.FromSqlInterpolated($"""
                SELECT * FROM "AutomationRules" WHERE "Id" = {ruleId} AND "WorkspaceId" = {workspace} FOR SHARE
                """).AsNoTracking().SingleOrDefaultAsync(token);
            if (rule is null || !rule.Enabled || rule.ArchivedAt is not null || rule.ActionTypeCode != "email.send"
                || AutomationActionSnapshot.Fingerprint(rule) != request.RuleFingerprint) throw Error("EmailRuleChanged");
        }
        if (plan.OpportunityId is { } opportunity && !await db.Opportunities.FromSqlInterpolated($"""
            SELECT * FROM "Opportunities" WHERE "Id" = {opportunity} AND "WorkspaceId" = {workspace} FOR SHARE
            """).AnyAsync(token) || plan.CompanyId is { } company && !await db.Companies.FromSqlInterpolated($"""
            SELECT * FROM "Companies" WHERE "Id" = {company} AND "WorkspaceId" = {workspace} FOR SHARE
            """).AnyAsync(token) || plan.ContactId is { } contact && !await db.Contacts.FromSqlInterpolated($"""
            SELECT * FROM "Contacts" WHERE "Id" = {contact} AND "WorkspaceId" = {workspace} FOR SHARE
            """).AnyAsync(token)) throw Error("EmailReferenceChanged");
        return (plan, account);
    }
    private async Task Human(Guid workspace, CancellationToken token)
    { if ((await human.RequireAsync(token)).WorkspaceId != workspace) throw new UnauthorizedAccessException(); }
    private async Task Lock(Guid workspace, CancellationToken token)
    { await EmailSyncLocks.WorkspaceAsync(db, workspace, token); await AutomationRuntimeLock.AcquireAsync(db, workspace, token); }
    private async Task<(EmailSendDelivery, IntegrationOperation)> Locked(Guid workspace, Guid id, CancellationToken token)
    {
        var d = await db.EmailSendDeliveries.FromSqlInterpolated($"""
            SELECT * FROM "EmailSendDeliveries" WHERE "Id" = {id} AND "WorkspaceId" = {workspace} FOR UPDATE
            """).AsNoTracking().SingleOrDefaultAsync(token) ?? throw Error("EmailDeliveryNotFound", 404);
        var op = await db.IntegrationOperations.FromSqlInterpolated($"""
            SELECT * FROM "IntegrationOperations" WHERE "Id" = {d.IntegrationOperationId} FOR UPDATE
            """).AsNoTracking().SingleAsync(token);
        if (op.WorkspaceId != workspace || op.ConnectionId != d.ConnectionId || op.Type != "email.send") throw Error("EmailDeliveryBindingInvalid");
        return (d, op);
    }
    private async Task Persist(EmailSendDelivery d, string operationStatus, CancellationToken token)
    {
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "EmailSendDeliveries" SET "StatusCode" = {d.StatusCode}, "StartedAt" = {d.StartedAt}, "FinishedAt" = {d.FinishedAt},
                "ExternalMessageId" = {d.ExternalMessageId}, "EmailMessageId" = {d.EmailMessageId}, "DiagnosticCode" = {d.DiagnosticCode} WHERE "Id" = {d.Id};
            UPDATE "IntegrationOperations" SET "StatusCode" = {operationStatus}, "CompletedAt" = {d.FinishedAt}, "Revision" = "Revision" + 1 WHERE "Id" = {d.IntegrationOperationId}
            """, token);
    }
    private static void Match(EmailSendDelivery d, IntegrationOperation op, EmailSendResult r)
    {
        if (r.DeliveryId != d.Id || r.OperationId != op.Id || r.CommandMessageId != op.CommandMessageId || r.CorrelationId != op.CorrelationId
            || r.IdempotencyKey != d.IdempotencyKey || r.Version != op.Version || r.Type != op.Type) throw Error("EmailEvidenceContextMismatch");
    }
    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static EmailSyncException Error(string code, int status = 409) => new(code, status);
}
