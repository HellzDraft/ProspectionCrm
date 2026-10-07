using Microsoft.Extensions.Options;

namespace ProspectionCrm.Api.Services.Collection;

public sealed class SourceCollectionWorker(IServiceScopeFactory scopes, IOptions<SourceCollectionWorkerOptions> options,
    ILogger<SourceCollectionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled) return;
        var idleDelay = TimeSpan.FromSeconds(options.Value.IdleDelaySeconds);
        while (!stoppingToken.IsCancellationRequested)
        {
            var processed = false;
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                processed = await scope.ServiceProvider.GetRequiredService<ISourceCollectionJobProcessor>()
                    .ProcessNextAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                logger.LogError(exception, "Collection worker cycle failed; next queue check follows the idle delay");
            }
            if (!processed)
            {
                try { await Task.Delay(idleDelay, stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            }
        }
    }
}
