using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ProspectionCrm.Api.Data;
using ProspectionCrm.Api.Dtos.Integrations;
using ProspectionCrm.Api.Entities;
using ProspectionCrm.Api.Security;
using ProspectionCrm.Api.Services.Integrations;

namespace ProspectionCrm.Api.Services.Collection;

public sealed record RemoteDispatchResult(bool Handled, string? ErrorCode = null);

public sealed class RemoteCollectionDispatcher(ProspectionCrmDbContext db, RemoteCollectionState state,
    IntegrationStore integrations, IntegrationTransport transport, IntegrationSendGate gate,
    IOptionsMonitor<IntegrationOptions> options, IOptionsMonitor<OperatorOptions> human)
{
    public async Task<RemoteDispatchResult> TryDispatchAsync(SourceCollectionJob job, CancellationToken token)
    {
        if (!await db.RemoteCollectionIntents.AnyAsync(x => x.JobId == job.Id, token)
            && !await db.SavedSearches.AnyAsync(x => x.Id == job.SavedSearchId && x.WorkspaceId == job.WorkspaceId
                && x.SourceConfiguration.SourceTypeCode == CollectionIntegrationContracts.SourceType, token)) return new(false);
        var config = options.CurrentValue;
        if (!config.Enabled || !config.IsValid(human.CurrentValue)) return new(true, "RemoteIntegrationDisabled");
        if (config.WorkspaceId != job.WorkspaceId) return new(true, "RemoteConnectionMismatch");
        if (!await gate.EnterAsync(token)) return new(true, "RemoteDispatchBusy");
        try
        {
            var intent = await state.PrepareAsync(job, config.ConnectionId, token);
            var attempt = await state.BeginAsync(job, intent, token);
            // Both transactions have committed before HTTP. A timeout/disconnect is uncertain.
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(config.TimeoutSeconds));
            var outcome = await transport.SendAsync(config, Encoding.UTF8.GetBytes(intent.CommandJson), deadline.Token);
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await integrations.FinishAttemptAsync(job.WorkspaceId, intent.OperationId, attempt.Id, outcome, cleanup.Token);
            return new(true, outcome == IntegrationTransportOutcome.Failed ? "RemoteDispatchFailed" : null);
        }
        catch (IntegrationConflictException error) { return new(true, error.Message); }
        catch (IntegrationAccessException) { return new(true, "RemoteConnectionMismatch"); }
        finally { gate.Exit(); }
    }
}
