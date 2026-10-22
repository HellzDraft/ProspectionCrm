using System.Globalization;
using Microsoft.EntityFrameworkCore;
using ProspectionCrm.Api.Dtos.CalendarEvents;
using ProspectionCrm.Api.Dtos.Integrations;
using ProspectionCrm.Api.Entities;

namespace ProspectionCrm.Api.Services.Calendar;

public sealed partial class CalendarEventDeliveryService
{
    // Never calls transport. Receipt processing and the business mutation commit together.
    public async Task ReconcileAsync(Guid workspace, Guid id, CancellationToken token = default)
    {
        await Human(workspace, token);
        await using var tx = await db.Database.BeginTransactionAsync(token); await Lock(workspace, token);
        await Human(workspace, token);
        var initial = await FindAsync(workspace, id, token) ?? throw Error("CalendarDeliveryNotFound", 404);
        var plan = await ApprovedPlan(workspace, initial.CalendarEventPlanId, token);
        // Compatible with sync: account -> calendar -> delivery/operation -> event.
        var (_, calendar) = await CalendarSyncLocks.CalendarAsync(db, workspace, initial.CalendarId, token);
        var (d, op) = await Locked(workspace, id, token); MatchPlan(d, plan, calendar);
        var receipts = await db.IntegrationReceipts.AsNoTracking().Where(x => x.OperationId == op.Id && x.ProcessedAt == null)
            .OrderBy(x => x.EvidenceNumber).ToListAsync(token);
        var now = await CalendarSyncLocks.Now(db, token);
        if (d.StatusCode == "conflict") return; // Sticky diagnosis and original receipts are retained for inspection.
        var proofs = new List<CalendarEventResult>();
        string? invalid = null; var decisiveHash = d.ProofHash;
        foreach (var receipt in receipts)
        {
            try
            {
                var proof = CalendarEventContracts.ParseResult(receipt.CanonicalContent); Match(d, op, proof);
                if (receipt.ContentSha256 != CalendarEventContracts.Hash(receipt.CanonicalContent) || receipt.MessageId != proof.MessageId
                    || d.StartedAt is null || proof.ProvenAt < d.StartedAt || proof.ProvenAt > now.AddMinutes(5))
                    throw Error("CalendarProviderProofInvalid");
                ValidateProof(d, CalendarEventSnapshots.From(plan), proof);
                if (proof.Outcome != "unknown")
                {
                    var hash = CalendarEventContracts.ProofHash(proof);
                    if (decisiveHash is not null && hash != decisiveHash) throw Error("CalendarEvidenceContradiction");
                    decisiveHash = hash;
                }
                proofs.Add(proof);
            }
            catch (Exception e) when (e is IntegrationContractException or CalendarSyncException)
            { invalid = e is CalendarSyncException ? e.Message : "CalendarProviderProofInvalid"; break; }
        }
        if (invalid is not null)
        {
            Conflict(d, invalid, now); await Persist(d, token); await tx.CommitAsync(token); return;
        }
        var decisive = proofs.FirstOrDefault(x => x.Outcome != "unknown");
        if (decisive is not null && d.ProofHash is null)
        {
            d.ProofHash = decisiveHash;
            if (decisive.Outcome == "applied")
            {
                // Preserve provider proof even when CRM cannot accept the mutation.
                d.ExternalEventId = decisive.Event!.ExternalEventId; d.ExternalRevision = decisive.Event.Revision;
                var error = await ApplyProof(d, plan, calendar, decisive.Event, token);
                if (error is not null) Conflict(d, error, now);
                else { d.StatusCode = "succeeded"; d.FinishedAt = now; d.DiagnosticCode = null; }
            }
            else if (decisive.Outcome == "version-conflict") Conflict(d, "CalendarProviderVersionConflict", now);
            else { d.StatusCode = "failed"; d.FinishedAt = now; d.DiagnosticCode = "CalendarProviderNotApplied"; }
        }
        else if (!Terminal(d) && (proofs.Count != 0 || d.StatusCode is "dispatching" or "awaiting-result"))
        {
            if (proofs.Count == 0 && clock.GetUtcNow() - d.StartedAt < TimeSpan.FromMinutes(2)) throw Error("CalendarAttemptNotRecoverable");
            d.StatusCode = "operation-unknown"; d.DiagnosticCode = "CalendarOperationUnknown";
        }
        foreach (var receipt in receipts)
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE "IntegrationReceipts" SET "ProcessedAt"={now} WHERE "MessageId"={receipt.MessageId}
                """, token);
        if (d.StatusCode == "operation-unknown")
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE "IntegrationAttempts" SET "StatusCode"='unknown', "FinishedAt"=GREATEST(statement_timestamp(),"StartedAt")
                WHERE "OperationId"={op.Id} AND "StatusCode"='started'
                """, token);
        if (receipts.Count != 0 || !Terminal(d)) await Persist(d, token);
        await tx.CommitAsync(token);
    }

    private static void ValidateProof(CalendarEventDelivery d, CalendarEventSnapshot snapshot, CalendarEventResult proof)
    {
        if (proof.Outcome == "version-conflict" && (snapshot.Target is null || proof.ObservedRevision == snapshot.Target.Revision))
            throw Error("CalendarConflictProofInvalid");
        if (proof.Event is not { } e) return;
        var c = snapshot.Content; var t = snapshot.Target;
        if (e.Title != c.Title || e.Description != c.Description || e.Location != c.Location
            || e.StartsAt != DateTimeOffset.Parse(c.StartsAt, CultureInfo.InvariantCulture) || e.EndsAt != DateTimeOffset.Parse(c.EndsAt, CultureInfo.InvariantCulture)
            || e.IsAllDay != c.IsAllDay || e.TimeZoneId != c.TimeZoneId || e.Status != "confirmed"
            || e.SeriesId != t?.SeriesId || e.OccurrenceId != t?.OccurrenceId
            || t is not null && (e.ExternalEventId != d.TargetExternalEventId || e.Revision <= t.Revision))
            throw Error("CalendarConfirmedContentMismatch");
    }

    private async Task<string?> ApplyProof(CalendarEventDelivery d, CalendarEventPlan plan, Entities.Calendar calendar, InboundCalendarEvent proof, CancellationToken token)
    {
        var old = await db.CalendarEvents.FromSqlInterpolated($"""
            SELECT * FROM "CalendarEvents" WHERE "CalendarId"={calendar.Id} AND "ExternalEventId"={proof.ExternalEventId} FOR UPDATE
            """).AsNoTracking().SingleOrDefaultAsync(token);
        var target = CalendarEventSnapshots.From(plan).Target;
        var exact = old is not null && old.ProviderCode == d.ProviderCode && old.ExternalRevision == proof.Revision
            && old.ExternalContentSha256 == InboundCalendarImportService.Hash(proof)
            && Value(old) == proof;
        if (target is not null)
        {
            if (old is null || old.Id != target.Id || old.OpportunityId != target.OpportunityId || old.ContactId != target.ContactId
                || old.SeriesId != target.SeriesId || old.OccurrenceId != target.OccurrenceId
                || !exact && CalendarEventSnapshots.Target(old) != target)
                return "CalendarReconciliationTargetConflict";
        }
        else if (old is not null && (!exact || old.OpportunityId is not null && old.OpportunityId != plan.OpportunityId
            || old.ContactId is not null && old.ContactId != plan.ContactId)) return "CalendarReconciliationIdentityConflict";
        var imported = await importer.ImportAsync(calendar, [proof], token);
        if (imported.ConflictId is not null) return "CalendarReconciliationContentConflict";
        var eventId = await db.CalendarEvents.Where(x => x.CalendarId == calendar.Id && x.ExternalEventId == proof.ExternalEventId).Select(x => x.Id).SingleAsync(token);
        if (target is null)
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE "CalendarEvents" SET "OpportunityId"={plan.OpportunityId}, "ContactId"={plan.ContactId} WHERE "Id"={eventId}
                """, token);
        d.CalendarEventId = eventId;
        return null;
    }
    private static InboundCalendarEvent Value(CalendarEvent e) => new(e.ExternalEventId!, e.ExternalRevision ?? -1,
        e.Title, e.Description, e.Location, e.StartsAt, e.EndsAt, e.IsAllDay, e.TimeZoneId, e.StatusCode!, e.SeriesId, e.OccurrenceId);
    private static void Conflict(CalendarEventDelivery d, string code, DateTimeOffset now)
    { d.StatusCode = "conflict"; d.FinishedAt ??= now; d.DiagnosticCode = code; }
}
