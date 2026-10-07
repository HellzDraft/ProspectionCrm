using System.Diagnostics;
using Microsoft.Extensions.Options;

namespace ProspectionCrm.Api.Services.Collection;

public interface ISourceCollectionJobProcessor
{
    Task<bool> ProcessNextAsync(CancellationToken token);
}

public sealed class SourceCollectionJobProcessor(ISourceCollectionJobQueue queue, ISourceCollectionOrchestrator orchestrator,
    IServiceScopeFactory scopes, IOptions<SourceCollectionWorkerOptions> options,
    ILogger<SourceCollectionJobProcessor> logger) : ISourceCollectionJobProcessor
{
    public async Task<bool> ProcessNextAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var claimStarted = Stopwatch.GetTimestamp();
        var job = await queue.ClaimAsync(token);
        if (job is null) return false;
        using var logScope = logger.BeginScope(new Dictionary<string, object>
        { ["JobId"] = job.Id, ["WorkspaceId"] = job.WorkspaceId, ["AttemptCount"] = job.AttemptCount });
        string? errorCode = null;
        // Charge claim latency to the budget, using a monotonic clock rather than assuming
        // the application's wall clock is synchronized with PostgreSQL's lease timestamps.
        var remaining = TimeSpan.FromSeconds(options.Value.LeaseDurationSeconds) - Stopwatch.GetElapsedTime(claimStarted);
        using var deadline = new CancellationTokenSource();
        if (remaining <= TimeSpan.Zero) deadline.Cancel();
        else deadline.CancelAfter(remaining);
        using var processing = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
        try
        {
            processing.Token.ThrowIfCancellationRequested();
            var attempt = new CollectionJobAttempt(job.Id, job.LeaseToken!.Value, Guid.NewGuid(), job.TriggerTypeCode);
            var result = await orchestrator.CollectAsync(job.WorkspaceId, job.SavedSearchId, job.PipelineStageId, attempt, processing.Token);
            errorCode = result.Error?.Code;
        }
        catch (OperationCanceledException) when (processing.IsCancellationRequested)
        {
            errorCode = token.IsCancellationRequested ? "CollectionWorkerStopping" : "CollectionLeaseExpired";
            logger.LogWarning("Collection interrupted: {ErrorCode}", errorCode);
        }
        catch (Exception exception)
        {
            errorCode = "CollectionWorkerError";
            logger.LogError(exception, "Unexpected collection processing failure: {ErrorCode}", errorCode);
        }

        // A failed/cancelled business scope must not poison finalization. Cleanup is bounded and
        // independent of the stopping token, just like existing ingestion history preservation.
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var completed = await scope.ServiceProvider.GetRequiredService<ISourceCollectionJobQueue>()
                .CompleteAsync(job.Id, job.WorkspaceId, job.LeaseToken!.Value, errorCode, cleanup.Token);
            if (!completed) logger.LogWarning("Collection job could not be completed because ownership changed");
        }
        catch (Exception exception)
        {
            // Leave the durable running lease for diagnosis/reconciliation; do not guess or re-enqueue.
            logger.LogError(exception, "Collection job completion could not be persisted; lease remains for reconciliation");
        }
        token.ThrowIfCancellationRequested();
        return true;
    }
}
