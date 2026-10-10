using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ProspectionCrm.Api.Data;
using ProspectionCrm.Api.Dtos.AutomationSupervision;
using ProspectionCrm.Api.Entities;

namespace ProspectionCrm.Api.Services.Automation;

// Read-only queries. The processor holds the workspace lock around guards and mutations.
public sealed class AutomationRuntimeGuard(ProspectionCrmDbContext db, AutomationCircuitBreakerService circuit,
    TimeProvider clock, IOptions<AutomationWorkerOptions> options)
{
    public async Task<AutomationRuntimeGuardSnapshot> ReadAsync(AutomationRuntimeSettings settings,
        DateTimeOffset now, CancellationToken token)
    {
        var status = await circuit.ReadAsync(settings, token);
        var day = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
        var effects = db.AutomationExecutions.AsNoTracking()
            .Where(x => x.WorkspaceId == settings.WorkspaceId && x.IsAutomaticAttempt && x.EffectApplied);
        var daily = await effects.LongCountAsync(x => x.FinishedAt >= day && x.FinishedAt < day.AddDays(1), token);
        var start = now.AddMinutes(-1);
        // Preserve the 8.4 conservative clock-rollback contract: a persisted future effect still counts.
        var minute = effects.Where(x => x.FinishedAt > start);
        var used = await minute.LongCountAsync(token);
        DateTimeOffset? available = null;
        if (used >= settings.MaxExecutionsPerMinute)
            available = (await minute.OrderByDescending(x => x.FinishedAt)
                .Skip(settings.MaxExecutionsPerMinute - 1).Select(x => x.FinishedAt).FirstAsync(token))?.AddMinutes(1);
        return new(status, new(new(used, settings.MaxExecutionsPerMinute, used >= settings.MaxExecutionsPerMinute, available),
            new(daily, settings.MaxExecutionsPerDay, daily >= settings.MaxExecutionsPerDay, day.AddDays(1))));
    }

    public async Task<(string Reason, DateTimeOffset Until)?> DeferredAsync(AutomationRuntimeSettings settings,
        CancellationToken token)
    {
        var now = clock.GetUtcNow().ToUniversalTime();
        if (!settings.IsEnabled) return (AutomationReasons.Disabled, now.AddSeconds(options.Value.DeferredDelaySeconds));
        var snapshot = await ReadAsync(settings, now, token);
        if (snapshot.Circuit.CanReset) return (AutomationAlertCodes.CircuitOpen, now.AddSeconds(options.Value.DeferredDelaySeconds));
        if (snapshot.Quotas.Day.IsReached) return ("daily-quota", snapshot.Quotas.Day.AvailableAgainAt!.Value);
        if (snapshot.Quotas.Minute.IsReached) return ("minute-quota", snapshot.Quotas.Minute.AvailableAgainAt!.Value);
        return null;
    }
}
