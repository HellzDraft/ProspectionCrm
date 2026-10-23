using Microsoft.EntityFrameworkCore;
using Npgsql;
using ProspectionCrm.Api.Data;
using ProspectionCrm.Api.Dtos.Integrations;
using ProspectionCrm.Api.Security;
using ProspectionCrm.Api.Services.Calendar;

namespace ProspectionCrm.Api.Services.Integrations;

// Dispatchers and transports are intentionally absent. Only the guarded reconciliation entry point is used.
public sealed class IntegrationRecoveryService(ProspectionCrmDbContext db, IHumanActor human,
    CalendarEventDeliveryService calendar, TimeProvider clock)
{
    public async Task<int> RecoverAsync(Guid id, IntegrationRecoveryRequest request, CancellationToken token)
    {
        var actor = await human.RequireAsync(token);
        var op = await db.IntegrationOperations.AsNoTracking().Where(x => x.Id == id && x.WorkspaceId == actor.WorkspaceId)
            .Select(x => new { x.Type }).SingleOrDefaultAsync(token);
        if (op is null) return 404;
        if (op.Type is not ("calendar.event.create" or "calendar.event.update"))
            return await Refuse("unsupported-family");
        // Only resolve the ID here. Every confirmation/binding/state check is repeated under the business locks.
        var delivery = await db.CalendarEventDeliveries.AsNoTracking()
            .Where(x => x.WorkspaceId == actor.WorkspaceId && x.IntegrationOperationId == id)
            .Select(x => (Guid?)x.Id).SingleOrDefaultAsync(token);
        if (delivery is null) return await Refuse("binding-unavailable");
        try
        {
            await calendar.RecoverRecordedEvidenceAsync(actor.WorkspaceId, delivery.Value, id, request, token);
            return 200;
        }
        catch (Exception e) when (e is CalendarSyncException or IntegrationContractException or DbUpdateConcurrencyException
            || IsConcurrencyRefusal(e))
        {
            // Business transaction has been disposed/rolled back before this separate refusal audit.
            db.ChangeTracker.Clear();
            return await Refuse("state-or-evidence-rejected");
        }
        async Task<int> Refuse(string outcome)
        {
            db.ActivityEntries.Add(IntegrationRecoveryAudit.Entry(actor.WorkspaceId, actor.UserId, id, outcome, clock.GetUtcNow()));
            await db.SaveChangesAsync(token);
            return 409;
        }
    }

    private static bool IsConcurrencyRefusal(Exception error)
    {
        // EF's execution strategy can wrap PostgreSQL transient errors in InvalidOperationException.
        for (Exception? cause = error; cause is not null; cause = cause.InnerException)
            if (cause is PostgresException { SqlState: "40001" or "40P01" or "55P03" }) return true;
        return false;
    }
}
