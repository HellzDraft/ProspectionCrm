using Microsoft.Extensions.Options;

namespace ProspectionCrm.Api.Services.Automation;

public interface IAutomationWorkerDelay
{
    Task WaitAsync(TimeSpan duration, CancellationToken token);
}
public sealed class AutomationWorkerDelay(TimeProvider clock) : IAutomationWorkerDelay
{
    public Task WaitAsync(TimeSpan duration, CancellationToken token) => Task.Delay(duration, clock, token);
}

public sealed class AutomationWorker(IServiceScopeFactory scopes, IOptions<AutomationWorkerOptions> options,
    TimeProvider clock, IAutomationWorkerDelay delay, ILogger<AutomationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled) return;
        var config = options.Value;
        Guid? cursor = null, recoveryCursor = null;
        var nextRecovery = DateTimeOffset.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            var worked = false;
            var wait = TimeSpan.FromSeconds(config.IdleDelaySeconds);
            try
            {
                if (clock.GetUtcNow() >= nextRecovery)
                {
                    var workspaces = await Find(recoveryCursor, true, stoppingToken);
                    foreach (var workspace in workspaces)
                    {
                        recoveryCursor = workspace;
                        await using var scope = scopes.CreateAsyncScope();
                        await scope.ServiceProvider.GetRequiredService<IAutomationJobRecovery>()
                            .RecoverAsync(workspace, stoppingToken);
                    }
                    recoveryCursor = workspaces.Count == config.WorkspaceBatchSize ? workspaces[^1] : null;
                    nextRecovery = clock.GetUtcNow().AddSeconds(config.RecoveryIntervalSeconds);
                }
                var ready = await Find(cursor, false, stoppingToken);
                foreach (var workspace in ready)
                {
                    cursor = workspace;
                    await using var scope = scopes.CreateAsyncScope();
                    var queue = scope.ServiceProvider.GetRequiredService<IAutomationJobQueue>();
                    var job = await queue.ClaimAsync(workspace, stoppingToken);
                    if (job is null) continue;
                    worked = true;
                    await scope.ServiceProvider.GetRequiredService<IAutomationJobProcessor>()
                        .ProcessAsync(job, job.LeaseOwner!, stoppingToken);
                }
                if (ready.Count < config.WorkspaceBatchSize) cursor = null;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception)
            {
                // No exception object: database diagnostics can contain payloads or credentials.
                logger.LogError("Automation cycle failed: {ReasonCode}", "worker-cycle-failed");
                worked = false;
                wait = TimeSpan.FromSeconds(config.ErrorDelaySeconds);
            }
            if (!worked)
            {
                try { await delay.WaitAsync(wait, stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            }
        }
    }

    private async Task<IReadOnlyList<Guid>> Find(Guid? after, bool expired, CancellationToken token)
    {
        await using var scope = scopes.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IAutomationWorkSource>()
            .FindAsync(after, options.Value.WorkspaceBatchSize, expired, token);
    }
}
