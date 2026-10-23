using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ProspectionCrm.Api.Data;
using ProspectionCrm.Api.Dtos.CalendarEvents;
using ProspectionCrm.Api.Dtos.Integrations;
using ProspectionCrm.Api.Entities;
using ProspectionCrm.Api.Security;
using ProspectionCrm.Api.Services.Automation;
using ProspectionCrm.Api.Services.Integrations;

namespace ProspectionCrm.Api.Services.Calendar;

public sealed partial class CalendarEventDeliveryService(ProspectionCrmDbContext db, CalendarEventPlanService plans,
    IAutomationSafetyPolicy policy, IHumanActor human, IOptionsMonitor<IntegrationOptions> integrations,
    IOptionsMonitor<OperatorOptions> operators, InboundCalendarImportService importer, TimeProvider clock)
{
    public Task<CalendarEventDelivery?> FindAsync(Guid workspace, Guid id, CancellationToken token = default) =>
        db.CalendarEventDeliveries.AsNoTracking().SingleOrDefaultAsync(x => x.WorkspaceId == workspace && x.Id == id, token);

    public async Task<CalendarEventDelivery> CreateAsync(Guid workspace, Guid planId, CancellationToken token = default)
    {
        await Human(workspace, token);
        await using var tx = await db.Database.BeginTransactionAsync(token); await Lock(workspace, token);
        await Human(workspace, token);
        // Repeated creation is a read of the existing intention, even after its update changed the target.
        var existing = await db.CalendarEventDeliveries.AsNoTracking().SingleOrDefaultAsync(x => x.WorkspaceId == workspace && x.CalendarEventPlanId == planId, token);
        if (existing is not null) return existing;
        var (plan, calendar) = await Authorize(workspace, planId, token);
        var now = await CalendarSyncLocks.Now(db, token);
        var op = new IntegrationOperation { Id = Guid.NewGuid(), WorkspaceId = workspace, ConnectionId = plan.ConnectionId, CommandMessageId = Guid.NewGuid(),
            CorrelationId = Guid.NewGuid(), Version = 1, Type = "calendar.event." + plan.OperationCode, StatusCode = "pending", CreatedAt = now };
        var target = CalendarEventSnapshots.From(plan).Target;
        var d = new CalendarEventDelivery { WorkspaceId = workspace, CalendarEventPlanId = planId,
            AutomationActionRequestId = plan.AutomationActionRequestId!.Value, CalendarAccountId = plan.CalendarAccountId,
            CalendarId = plan.CalendarId, ConnectionId = plan.ConnectionId, IntegrationOperationId = op.Id, OperationType = op.Type,
            ProviderCode = calendar.ProviderCode, ExternalCalendarId = calendar.ExternalCalendarId, ContentHash = plan.ContentHash,
            IdempotencyKey = "", TargetCalendarEventId = target?.Id, TargetExternalEventId = target?.ExternalEventId,
            TargetExternalRevision = target?.Revision, TargetContentHash = target?.ContentHash, CreatedAt = now };
        d.IdempotencyKey = $"calendar-event-{d.Id:D}";
        _ = CalendarEventContracts.Serialize(Command(d, op, plan));
        db.AddRange(op, d); await db.SaveChangesAsync(token); await tx.CommitAsync(token);
        db.Entry(op).State = EntityState.Detached; db.Entry(d).State = EntityState.Detached;
        return d;
    }

    internal async Task<(CalendarEventCommand Command, Guid Attempt)> BeginAsync(Guid workspace, Guid id, CancellationToken token)
    {
        await Human(workspace, token);
        await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, token); await Lock(workspace, token);
        await Human(workspace, token);
        var initial = await FindAsync(workspace, id, token) ?? throw Error("CalendarDeliveryNotFound", 404);
        var (plan, calendar) = await Authorize(workspace, initial.CalendarEventPlanId, token);
        var (d, op) = await Locked(workspace, id, token);
        MatchPlan(d, plan, calendar);
        if (d.StatusCode != "pending" || op.StatusCode != "pending" || await db.IntegrationAttempts.AnyAsync(x => x.OperationId == op.Id, token))
            throw Error("CalendarDeliveryNotDispatchable");
        var command = Command(d, op, plan);
        _ = CalendarEventContracts.Serialize(command);
        var (now, refusal) = await IntegrationDispatchBudget.CheckUnderRuntimeLockAsync(db, workspace, d.ConnectionId,
            integrations.CurrentValue.DispatchLimits, token);
        if (refusal is not null) throw Error(refusal);
        var attempt = Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "IntegrationAttempts" ("Id","OperationId","Number","StatusCode","StartedAt") VALUES ({attempt},{op.Id},1,'started',{now})
            """, token);
        d.StartedAt = now; d.StatusCode = "dispatching";
        await Persist(d, token); await tx.CommitAsync(token);
        return (command, attempt);
    }

    internal async Task CompleteAsync(Guid workspace, Guid id, Guid attempt, CalendarEventTransportOutcome outcome, CancellationToken token)
    {
        await using var tx = await db.Database.BeginTransactionAsync(token); await Lock(workspace, token);
        var (d, op) = await Locked(workspace, id, token);
        var status = outcome == CalendarEventTransportOutcome.Acknowledged ? "acknowledged" : "unknown";
        var changed = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "IntegrationAttempts" SET "StatusCode"={status}, "FinishedAt"=GREATEST(statement_timestamp(),"StartedAt")
            WHERE "Id"={attempt} AND "OperationId"={op.Id} AND "StatusCode"='started'
            """, token);
        if (changed != 0 && d.StatusCode == "dispatching")
        {
            d.StatusCode = outcome == CalendarEventTransportOutcome.Acknowledged ? "awaiting-result" : "operation-unknown";
            d.DiagnosticCode = outcome == CalendarEventTransportOutcome.Acknowledged ? null : "CalendarOperationUnknown";
            await Persist(d, token);
        }
        await tx.CommitAsync(token);
    }

    // Only IntegrationStore routes authenticated HTTPS/HMAC callbacks here. Scope comes from verified claims.
    public async Task<IntegrationReception> ReceiveAsync(IntegrationAccess access, string json, CancellationToken token = default)
    {
        var result = CalendarEventContracts.ParseResult(json); var canonical = CalendarEventContracts.Serialize(result);
        var initial = await FindAsync(access.WorkspaceId, result.DeliveryId, token);
        if (initial is null || access.ConnectionId == Guid.Empty || initial.ConnectionId != access.ConnectionId) throw new IntegrationAccessException();
        await using var tx = await db.Database.BeginTransactionAsync(token); await Lock(access.WorkspaceId, token);
        var (d, op) = await Locked(access.WorkspaceId, result.DeliveryId, token); Match(d, op, result);
        var prior = await db.IntegrationReceipts.AsNoTracking().SingleOrDefaultAsync(x => x.MessageId == result.MessageId, token);
        if (prior is not null)
        {
            if (prior.OperationId != op.Id || prior.CanonicalContent != canonical || prior.ContentSha256 != CalendarEventContracts.Hash(canonical))
                throw Error("CalendarEvidenceReplayConflict");
            return IntegrationReception.AlreadyRecorded;
        }
        if (d.StartedAt is null) throw Error("CalendarEvidenceBeforeDispatch");
        var number = (await db.IntegrationReceipts.Where(x => x.OperationId == op.Id).MaxAsync(x => (int?)x.EvidenceNumber, token) ?? 0) + 1;
        if (number > 100) throw Error("CalendarEvidenceLimit");
        var now = await CalendarSyncLocks.Now(db, token);
        var inserted = await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "IntegrationReceipts" ("MessageId","OperationId","EvidenceNumber","CanonicalContent","ContentSha256","ReceivedAt")
            VALUES ({result.MessageId},{op.Id},{number},{canonical},{CalendarEventContracts.Hash(canonical)},{now}) ON CONFLICT ("MessageId") DO NOTHING
            """, token);
        if (inserted != 1) throw Error("CalendarEvidenceReplayConflict");
        if (!Terminal(d)) { d.StatusCode = "result-received"; await Persist(d, token); }
        await tx.CommitAsync(token); return IntegrationReception.Recorded;
    }

    // A second durable check immediately before HTTP. The transaction ends before any network I/O.
    internal async Task ValidateTransportAsync(CalendarEventCommand command, Guid workspace, Guid connection, CancellationToken token)
    {
        if (command.Snapshot.WorkspaceId != workspace || command.Snapshot.ConnectionId != connection)
            throw Error("CalendarDeliveryBindingInvalid");
        await using var tx = await db.Database.BeginTransactionAsync(token); await Lock(workspace, token);
        var initial = await FindAsync(workspace, command.DeliveryId, token) ?? throw Error("CalendarDeliveryNotFound");
        var (plan, calendar) = await Authorize(workspace, initial.CalendarEventPlanId, token, initial.Id);
        var (d, op) = await Locked(workspace, initial.Id, token); MatchPlan(d, plan, calendar);
        if (d.ConnectionId != connection || d.StatusCode != "dispatching" || op.StatusCode != "awaiting-result"
            || !await db.IntegrationAttempts.AnyAsync(x => x.OperationId == op.Id && x.Number == 1 && x.StatusCode == "started", token)
            || CalendarEventContracts.Serialize(command) != CalendarEventContracts.Serialize(Command(d, op, plan)))
            throw Error("CalendarDeliveryBindingInvalid");
        await tx.CommitAsync(token);
    }

    private async Task<(CalendarEventPlan, Entities.Calendar)> Authorize(Guid workspace, Guid id, CancellationToken token, Guid? currentAttempt = null)
    {
        var plan = await ApprovedPlan(workspace, id, token);
        var config = integrations.CurrentValue;
        if (!config.Enabled || !config.IsValid(operators.CurrentValue)) throw Error("CalendarExecutionDisabled");
        // Reuse approval's canonical content, references, account/calendar and full target validation.
        await plans.Revalidate(plan, token);
        var calendar = await db.Calendars.AsNoTracking().SingleAsync(x => x.Id == plan.CalendarId, token);
        var settings = await db.AutomationRuntimeSettings.FromSqlInterpolated($"""
            SELECT * FROM "AutomationRuntimeSettings" WHERE "WorkspaceId"={workspace} FOR SHARE
            """).AsNoTracking().SingleOrDefaultAsync(token);
        if (settings is null || policy.Evaluate(settings, AutomationCategories.Calendar).DecisionCode != AutomationDecisions.Manual)
            throw Error("CalendarExecutionDisabled");
        if ((await new AutomationCircuitBreakerService(db, clock).ReadAsync(settings, token)).CanReset) throw Error("CalendarCircuitOpen");
        var now = await CalendarSyncLocks.Now(db, token);
        // Workspace execution quotas include other authorized effects and email sends.
        async Task<int> Count(DateTimeOffset since) => await db.CalendarEventDeliveries.CountAsync(x => x.WorkspaceId == workspace && x.StartedAt >= since && x.Id != currentAttempt, token)
            + await db.EmailSendDeliveries.CountAsync(x => x.WorkspaceId == workspace && x.StartedAt >= since, token)
            + await db.AutomationExecutions.CountAsync(x => x.WorkspaceId == workspace && x.IsAutomaticAttempt && x.StartedAt >= since, token);
        if (await Count(now.AddMinutes(-1)) >= settings.MaxExecutionsPerMinute || await Count(now.AddDays(-1)) >= settings.MaxExecutionsPerDay)
            throw Error("CalendarExecutionRateLimited");
        return (plan, calendar);
    }

    private async Task<CalendarEventPlan> ApprovedPlan(Guid workspace, Guid id, CancellationToken token)
    {
        var requestId = await db.CalendarEventPlans.AsNoTracking().Where(x => x.Id == id && x.WorkspaceId == workspace)
            .Select(x => x.AutomationActionRequestId).SingleOrDefaultAsync(token);
        var request = await db.AutomationActionRequests.FromSqlInterpolated($"""
            SELECT * FROM "AutomationActionRequests" WHERE "Id"={requestId} AND "WorkspaceId"={workspace} FOR SHARE
            """).AsNoTracking().SingleOrDefaultAsync(token);
        var plan = await db.CalendarEventPlans.FromSqlInterpolated($"""
            SELECT * FROM "CalendarEventPlans" WHERE "Id"={id} AND "WorkspaceId"={workspace} FOR SHARE
            """).AsNoTracking().SingleOrDefaultAsync(token) ?? throw Error("CalendarEventPlanNotFound", 404);
        var now = await CalendarSyncLocks.Now(db, token);
        if (plan.StatusCode != "approved" || request is null || request.StatusCode != "approved" || request.DecidedAt is null
            || request.DecidedAt < request.RequestedAt || request.DecidedAt > now || plan.ApprovedAt is null
            || request.DecidedAt < plan.ApprovedAt || plan.AutomationActionRequestId != request.Id
            || !await db.Workspaces.AnyAsync(x => x.Id == workspace && x.OwnerUserId == request.DecidedByUserId
                && db.UserAccounts.Any(u => u.Id == x.OwnerUserId), token)
            || !CalendarEventSnapshots.ValidRequest(request) || request.DecisionRequirementCode != AutomationDecisions.Manual
            || request.ContentFingerprint != plan.ContentHash || CalendarEventSnapshots.Hash(CalendarEventSnapshots.From(plan)) != plan.ContentHash
            || CalendarEventSnapshots.Serialize(CalendarEventSnapshots.Parse(request.ActionPlanJson)!) != CalendarEventSnapshots.Serialize(CalendarEventSnapshots.From(plan)))
            throw Error("CalendarApprovalInvalid");
        return plan;
    }
    private async Task Human(Guid workspace, CancellationToken token)
    {
        var actor = await human.RequireAsync(token);
        if (actor.WorkspaceId != workspace || !await db.Workspaces.AnyAsync(x => x.Id == workspace && x.OwnerUserId == actor.UserId, token))
            throw new UnauthorizedAccessException();
    }
    private async Task Lock(Guid workspace, CancellationToken token)
    { await CalendarSyncLocks.WorkspaceAsync(db, workspace, token); await AutomationRuntimeLock.AcquireAsync(db, workspace, token); }
    private async Task<(CalendarEventDelivery, IntegrationOperation)> Locked(Guid workspace, Guid id, CancellationToken token)
    {
        var d = await db.CalendarEventDeliveries.FromSqlInterpolated($"""
            SELECT * FROM "CalendarEventDeliveries" WHERE "Id"={id} AND "WorkspaceId"={workspace} FOR UPDATE
            """).AsNoTracking().SingleOrDefaultAsync(token) ?? throw Error("CalendarDeliveryNotFound", 404);
        var op = await db.IntegrationOperations.FromSqlInterpolated($"""
            SELECT * FROM "IntegrationOperations" WHERE "Id"={d.IntegrationOperationId} FOR UPDATE
            """).AsNoTracking().SingleAsync(token);
        if (op.WorkspaceId != workspace || op.ConnectionId != d.ConnectionId || op.Type != d.OperationType) throw Error("CalendarDeliveryBindingInvalid");
        return (d, op);
    }
    private async Task Persist(CalendarEventDelivery d, CancellationToken token)
    {
        var status = d.StatusCode switch { "succeeded" => "succeeded", "failed" or "conflict" => "failed", "pending" => "pending",
            "result-received" => "result-received", _ => "awaiting-result" };
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "CalendarEventDeliveries" SET "StatusCode"={d.StatusCode}, "StartedAt"={d.StartedAt}, "FinishedAt"={d.FinishedAt},
                "ExternalEventId"={d.ExternalEventId}, "ExternalRevision"={d.ExternalRevision}, "CalendarEventId"={d.CalendarEventId},
                "ProofHash"={d.ProofHash}, "DiagnosticCode"={d.DiagnosticCode} WHERE "Id"={d.Id};
            UPDATE "IntegrationOperations" SET "StatusCode"={status}, "CompletedAt"={d.FinishedAt}, "Revision"="Revision"+1 WHERE "Id"={d.IntegrationOperationId}
            """, token);
    }
    private static CalendarEventCommand Command(CalendarEventDelivery d, IntegrationOperation op, CalendarEventPlan p) => new(1,
        op.CommandMessageId, op.Id, op.CorrelationId, op.Type, d.Id, d.IdempotencyKey, d.ContentHash, d.ProviderCode, d.ExternalCalendarId,
        CalendarEventSnapshots.From(p), d.TargetExternalEventId is null ? null : new(d.CalendarId, d.ExternalCalendarId,
            d.TargetExternalEventId, d.TargetExternalRevision!.Value, d.TargetContentHash!), "disabled");
    private static void MatchPlan(CalendarEventDelivery d, CalendarEventPlan p, Entities.Calendar c)
    {
        var target = CalendarEventSnapshots.From(p).Target;
        if (d.ContentHash != p.ContentHash || d.AutomationActionRequestId != p.AutomationActionRequestId || d.CalendarAccountId != p.CalendarAccountId
            || d.CalendarId != p.CalendarId || d.ConnectionId != p.ConnectionId || d.ProviderCode != c.ProviderCode || d.ExternalCalendarId != c.ExternalCalendarId
            || d.OperationType != "calendar.event." + p.OperationCode || d.TargetCalendarEventId != target?.Id || d.TargetExternalEventId != target?.ExternalEventId
            || d.TargetExternalRevision != target?.Revision || d.TargetContentHash != target?.ContentHash) throw Error("CalendarDeliveryBindingInvalid");
    }
    private static void Match(CalendarEventDelivery d, IntegrationOperation op, CalendarEventResult r)
    {
        if (r.OperationId != op.Id || r.CorrelationId != op.CorrelationId || r.CommandMessageId != op.CommandMessageId || r.Version != op.Version
            || r.Type != op.Type || r.DeliveryId != d.Id || r.IdempotencyKey != d.IdempotencyKey || r.ContentHash != d.ContentHash
            || r.CalendarId != d.CalendarId || r.ExternalCalendarId != d.ExternalCalendarId || r.ProviderCode != d.ProviderCode)
            throw Error("CalendarEvidenceContextMismatch");
    }
    private static bool Terminal(CalendarEventDelivery d) => d.StatusCode is "succeeded" or "failed" or "conflict";
    private static CalendarSyncException Error(string code, int status = 409) => new(code, status);
}
