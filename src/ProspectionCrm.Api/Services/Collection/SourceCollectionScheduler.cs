using Microsoft.Extensions.Options;

namespace ProspectionCrm.Api.Services.Collection;

public sealed class SourceCollectionScheduler(IServiceScopeFactory scopes, IOptions<SourceCollectionSchedulerOptions> options,
    ILogger<SourceCollectionScheduler> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<ISourceCollectionSchedulingService>().ScheduleDueAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception) { logger.LogError(exception, "Collection scheduler cycle failed"); }
            // Bounded work and a delay even for a full batch: no busy loop during a backlog or outage.
            try { await Task.Delay(TimeSpan.FromSeconds(options.Value.PollIntervalSeconds), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
