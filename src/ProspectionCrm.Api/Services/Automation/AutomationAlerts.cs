using ProspectionCrm.Api.Dtos.AutomationSupervision;

namespace ProspectionCrm.Api.Services.Automation;

public static class AutomationAlertSeverities
{
    public const string Info = "info", Warning = "warning", Critical = "critical";
}
public static class AutomationAlertCodes
{
    public const string CircuitOpen = "circuit-open", FailuresAccumulating = "automatic-failures-accumulating",
        ExpiredLeases = "expired-leases", AbandonedExecutions = "abandoned-running-executions",
        WorkerDisabled = "worker-disabled-with-open-work", RuntimeDisabled = "runtime-disabled-with-open-work",
        FailedJobs = "failed-jobs", PendingBacklog = "pending-job-backlog", ApprovalBacklog = "approval-backlog",
        StaleRequests = "stale-approval-requests", MinuteQuota = "minute-quota-reached", DailyQuota = "daily-quota-reached";
}

public static class AutomationAlerts
{
    public static IReadOnlyList<AutomationAlertDto> Calculate(AutomationSupervisionDto snapshot,
        AutomationSupervisionOptions options)
    {
        var alerts = new List<AutomationAlertDto>();
        void Add(string code, string severity, long count, DateTimeOffset? since = null, string? state = null)
        {
            if (count > 0) alerts.Add(new(code, severity, count, since, state));
        }
        var q = snapshot.Queue; var r = snapshot.ActionRequests; var c = snapshot.Circuit;
        if (c.CanReset) Add(AutomationAlertCodes.CircuitOpen, AutomationAlertSeverities.Critical,
            c.ConsecutiveFailureCount, c.OpenedAt, c.StatusCode);
        else Add(AutomationAlertCodes.FailuresAccumulating, AutomationAlertSeverities.Warning,
            c.ConsecutiveFailureCount, c.LastAutomaticFailureAt, c.StatusCode);
        Add(AutomationAlertCodes.ExpiredLeases, AutomationAlertSeverities.Critical, q.LeasedExpiredCount, state: "leased");
        Add(AutomationAlertCodes.AbandonedExecutions, AutomationAlertSeverities.Critical,
            snapshot.Executions.AbandonedRunningCount, state: "running");
        var work = q.PendingAvailableCount + q.PendingScheduledCount + q.LeasedActiveCount + q.LeasedExpiredCount;
        if (!snapshot.Worker.ConfiguredEnabled) Add(AutomationAlertCodes.WorkerDisabled,
            AutomationAlertSeverities.Warning, work, q.OldestOpenJobAt);
        if (!snapshot.RuntimeSettings.IsEnabled) Add(AutomationAlertCodes.RuntimeDisabled,
            AutomationAlertSeverities.Warning, work, q.OldestOpenJobAt);
        Add(AutomationAlertCodes.FailedJobs, AutomationAlertSeverities.Warning, q.FailedCount, state: "failed");
        var pending = q.PendingAvailableCount + q.PendingScheduledCount;
        if (pending >= options.BacklogWarningCount
            || q.OldestPendingAvailableAt <= snapshot.GeneratedAt.AddMinutes(-options.PendingJobWarningAgeMinutes))
            Add(AutomationAlertCodes.PendingBacklog, AutomationAlertSeverities.Warning, pending, q.OldestPendingAvailableAt, "pending");
        if (r.PendingCount >= options.BacklogWarningCount
            || r.OldestPendingAt <= snapshot.GeneratedAt.AddHours(-options.ApprovalWarningAgeHours))
            Add(AutomationAlertCodes.ApprovalBacklog, AutomationAlertSeverities.Warning, r.PendingCount, r.OldestPendingAt, "pending");
        Add(AutomationAlertCodes.StaleRequests, AutomationAlertSeverities.Warning, r.PendingStaleCount, state: "pending");
        if (snapshot.Quotas.Minute.IsReached) Add(AutomationAlertCodes.MinuteQuota, AutomationAlertSeverities.Warning, snapshot.Quotas.Minute.Used);
        if (snapshot.Quotas.Day.IsReached) Add(AutomationAlertCodes.DailyQuota, AutomationAlertSeverities.Warning, snapshot.Quotas.Day.Used);
        return alerts.OrderBy(x => x.SeverityCode switch { AutomationAlertSeverities.Critical => 0,
                AutomationAlertSeverities.Warning => 1, _ => 2 })
            .ThenBy(x => x.Code, StringComparer.Ordinal).ToArray();
    }
}
