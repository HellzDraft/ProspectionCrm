using ProspectionCrm.Api.Dtos.AutomationSupervision;
using ProspectionCrm.Api.Services.Automation;
using Xunit;

namespace ProspectionCrm.Api.Tests;

public sealed class AutomationSupervisionPureTests
{
    private static readonly DateTimeOffset Now = new(2030,1,2,12,0,0,TimeSpan.Zero);
    private static AutomationSupervisionDto Empty() => new(Guid.NewGuid(), Now,
        new(true,5,30,3), new(true,"automatic",10,100,3),
        new(0,0,0,0,0,0,0,0,null,null,null,null), new(0,0,0,0,null,null),
        new(0,0,0,0,0,0,0,0,null,null), new(new(0,10,false,null),new(0,100,false,Now.AddHours(12))),
        new(Guid.NewGuid(),3,0,null,null,null,null,null,null,null,null,null,0), []);

    [Theory]
    [InlineData("circuit-open","critical")][InlineData("automatic-failures-accumulating","warning")]
    [InlineData("expired-leases","critical")][InlineData("abandoned-running-executions","critical")]
    [InlineData("worker-disabled-with-open-work","warning")][InlineData("runtime-disabled-with-open-work","warning")]
    [InlineData("failed-jobs","warning")][InlineData("pending-job-backlog","warning")]
    [InlineData("approval-backlog","warning")][InlineData("stale-approval-requests","warning")]
    [InlineData("minute-quota-reached","warning")][InlineData("daily-quota-reached","warning")]
    public void EveryAlertHasAStableCodeSeverityAndPositiveCount(string code, string severity)
    {
        var s = Empty();
        s = code switch
        {
            AutomationAlertCodes.CircuitOpen => s with { Circuit = s.Circuit with { ConsecutiveFailureCount = 3, OpenedAt = Now } },
            AutomationAlertCodes.FailuresAccumulating => s with { Circuit = s.Circuit with { ConsecutiveFailureCount = 2 } },
            AutomationAlertCodes.ExpiredLeases => s with { Queue = s.Queue with { LeasedExpiredCount = 1 } },
            AutomationAlertCodes.AbandonedExecutions => s with { Executions = s.Executions with { AbandonedRunningCount = 1 } },
            AutomationAlertCodes.WorkerDisabled => s with { Worker = s.Worker with { ConfiguredEnabled = false }, Queue = s.Queue with { PendingScheduledCount = 1 } },
            AutomationAlertCodes.RuntimeDisabled => s with { RuntimeSettings = s.RuntimeSettings with { IsEnabled = false }, Queue = s.Queue with { LeasedActiveCount = 1 } },
            AutomationAlertCodes.FailedJobs => s with { Queue = s.Queue with { FailedCount = 1 } },
            AutomationAlertCodes.PendingBacklog => s with { Queue = s.Queue with { PendingScheduledCount = 100 } },
            AutomationAlertCodes.ApprovalBacklog => s with { ActionRequests = s.ActionRequests with { PendingCount = 100 } },
            AutomationAlertCodes.StaleRequests => s with { ActionRequests = s.ActionRequests with { PendingCount = 1, PendingStaleCount = 1 } },
            AutomationAlertCodes.MinuteQuota => s with { Quotas = s.Quotas with { Minute = new(10,10,true,Now.AddMinutes(1)) } },
            _ => s with { Quotas = s.Quotas with { Day = new(100,100,true,Now.AddHours(12)) } }
        };
        var alert = Assert.Single(AutomationAlerts.Calculate(s, new()));
        Assert.Equal(code, alert.Code); Assert.Equal(severity, alert.SeverityCode); Assert.True(alert.Count > 0);
        if (code == AutomationAlertCodes.CircuitOpen) Assert.Equal(Now, alert.Since);
    }

    [Theory]
    [InlineData(false,-1,false)][InlineData(false,0,true)][InlineData(false,1,true)]
    [InlineData(true,-1,false)][InlineData(true,0,true)][InlineData(true,1,true)]
    public void BacklogAgeBoundariesAreInclusive(bool human, int secondsBeyondBoundary, bool expected)
    {
        var s = Empty();
        s = human ? s with { ActionRequests = s.ActionRequests with {
            PendingCount = 1, OldestPendingAt = Now.AddHours(-24).AddSeconds(-secondsBeyondBoundary) } }
            : s with { Queue = s.Queue with {
                PendingAvailableCount = 1, OldestPendingAvailableAt = Now.AddMinutes(-30).AddSeconds(-secondsBeyondBoundary) } };
        Assert.Equal(expected, AutomationAlerts.Calculate(s,new()).Any());
    }

    [Fact]
    public void AlertsAreSortedCriticalThenWarningThenOrdinalCodeAndNeverHaveZeroCount()
    {
        var s = Empty();
        s = s with { Queue = s.Queue with { LeasedExpiredCount = 2, FailedCount = 5, PendingAvailableCount = 100 },
            Circuit = s.Circuit with { ConsecutiveFailureCount = 3, OpenedAt = Now },
            Executions = s.Executions with { AbandonedRunningCount = 1 } };
        var alerts = AutomationAlerts.Calculate(s,new());
        Assert.Equal(new[] { "abandoned-running-executions","circuit-open","expired-leases","failed-jobs","pending-job-backlog" },
            alerts.Select(x => x.Code));
        Assert.All(alerts, x => Assert.True(x.Count > 0));
        Assert.Empty(AutomationAlerts.Calculate(Empty(),new()));
        var waiting = Empty() with { Worker = new(false,5,30,3), RuntimeSettings = new(false,"manual",10,100,3),
            Queue = Empty().Queue with { AwaitingApprovalCount = 1 } };
        Assert.Empty(AutomationAlerts.Calculate(waiting,new()));
    }

    [Theory]
    [InlineData(0,3,false)][InlineData(1,3,false)][InlineData(2,3,false)][InlineData(3,3,true)]
    [InlineData(4,3,true)][InlineData(3,4,false)][InlineData(3,2,true)]
    public void CircuitStatusIsDerivedFromTheCurrentThreshold(long count, int threshold, bool open)
    {
        var c = Empty().Circuit with { ConsecutiveFailureCount = count, MaxConsecutiveFailures = threshold };
        Assert.Equal(open, c.CanReset); Assert.Equal(open ? "open" : "closed", c.StatusCode);
    }

    [Theory]
    [InlineData("pending",0,false)][InlineData("pending",1,true)][InlineData("pending",10080,true)][InlineData("pending",10081,false)]
    [InlineData("approval",0,false)][InlineData("approval",8760,true)][InlineData("approval",8761,false)]
    [InlineData("backlog",0,false)][InlineData("backlog",1000000,true)][InlineData("backlog",1000001,false)]
    [InlineData("failures",0,false)][InlineData("failures",8760,true)][InlineData("failures",8761,false)]
    public void SupervisionOptionsAreBounded(string field,int value,bool valid)
    {
        var o = new AutomationSupervisionOptions();
        switch (field)
        {
            case "pending": o.PendingJobWarningAgeMinutes = value; break;
            case "approval": o.ApprovalWarningAgeHours = value; break;
            case "backlog": o.BacklogWarningCount = value; break;
            default: o.RecentFailureWindowHours = value; break;
        }
        Assert.Equal(valid,o.IsValid());
    }
}
