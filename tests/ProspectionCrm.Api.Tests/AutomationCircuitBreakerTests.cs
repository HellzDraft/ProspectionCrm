using Microsoft.EntityFrameworkCore;
using ProspectionCrm.Api.Services.Automation;
using Xunit;

namespace ProspectionCrm.Api.Tests;

public sealed class AutomationCircuitBreakerTests(AutomationJobDatabase database)
    : AutomationSupervisionFixture(database), IClassFixture<AutomationJobDatabase>
{
    [Theory]
    [InlineData(0, 3, "closed")][InlineData(1, 3, "closed")][InlineData(2, 3, "closed")]
    [InlineData(3, 3, "open")][InlineData(5, 3, "open")]
    public async Task ExactConsecutiveCountAndThresholdCrossing(int failures, int threshold, string state)
    {
        var setup = await Prepare(); await Settings(setup.Workspace, s => s.MaxConsecutiveFailures = threshold);
        for (var i = 1; i <= failures; i++) await Outcome(setup, i, false, Clock.Now.AddMinutes(i));
        var status = await Circuit(setup.Workspace);
        Assert.Equal(state, status.StatusCode); Assert.Equal(failures, status.ConsecutiveFailureCount);
        Assert.Equal(state == "open", status.CanReset);
        Assert.Equal(failures == 0 ? (long?)null : failures, status.LastAutomaticOutcomeSequence);
        Assert.Equal(state == "open" ? Clock.Now.AddMinutes(threshold) : (DateTimeOffset?)null, status.OpenedAt);
        Assert.Null(status.LastResetId); Assert.Equal(0, status.ResetAfterOutcomeSequence);
    }

    [Fact]
    public async Task SequenceWinsOverTimestampsAndSuccessBreaksTheStreak()
    {
        var setup = await Prepare(); await Settings(setup.Workspace, s => s.MaxConsecutiveFailures = 2);
        await Outcome(setup, 1, false, Clock.Now.AddHours(5));
        await Outcome(setup, 2, false, Clock.Now.AddHours(4));
        Assert.Equal(Clock.Now.AddHours(4), (await Circuit(setup.Workspace)).OpenedAt);
        await Outcome(setup, 3, true, Clock.Now.AddHours(3));
        var success = await Circuit(setup.Workspace);
        Assert.Equal("closed", success.StatusCode); Assert.Equal(0, success.ConsecutiveFailureCount);
        await Outcome(setup, 4, false, Clock.Now.AddHours(2));
        Assert.Equal(1, (await Circuit(setup.Workspace)).ConsecutiveFailureCount);
        await Outcome(setup, 5, false, Clock.Now.AddHours(1));
        var status = await Circuit(setup.Workspace);
        Assert.Equal("open", status.StatusCode); Assert.Equal(2, status.ConsecutiveFailureCount);
        Assert.Equal(Clock.Now.AddHours(1), status.OpenedAt);
        Assert.Equal(Clock.Now.AddHours(1), status.LastAutomaticOutcomeAt);
        Assert.Equal(Clock.Now.AddHours(3), status.LastAutomaticSuccessAt);
        Assert.Equal(Clock.Now.AddHours(1), status.LastAutomaticFailureAt);
    }

    [Fact]
    public async Task CurrentThresholdRecomputesOpenedAtWithoutRewritingHistory()
    {
        var setup = await Prepare();
        for (var i = 1; i <= 3; i++) await Outcome(setup, i, false, Clock.Now.AddMinutes(i));
        await Settings(setup.Workspace, s => s.MaxConsecutiveFailures = 4);
        Assert.Equal("closed", (await Circuit(setup.Workspace)).StatusCode);
        await Settings(setup.Workspace, s => s.MaxConsecutiveFailures = 2);
        Assert.Equal(Clock.Now.AddMinutes(2), (await Circuit(setup.Workspace)).OpenedAt);
        await Settings(setup.Workspace, s => s.MaxConsecutiveFailures = 3);
        Assert.Equal(Clock.Now.AddMinutes(3), (await Circuit(setup.Workspace)).OpenedAt);
        await using var db = Db();
        Assert.Equal(3, await db.AutomationExecutions.CountAsync(x => x.WorkspaceId == setup.Workspace));
        Assert.Empty(await db.AutomationCircuitResets.Where(x => x.WorkspaceId == setup.Workspace).ToListAsync());
    }

    [Fact]
    public async Task ResetExcludesOldOutcomesAndNewFailuresCanReopenIndependently()
    {
        var a = await Prepare(); var b = await Prepare();
        await Settings(a.Workspace, s => s.MaxConsecutiveFailures = 2);
        await Outcome(a, 1, false); await Outcome(a, 2, false);
        await Outcome(b, 1, false);
        var reset = await Reset(a.Workspace);
        Assert.True(reset.WasReset); Assert.Equal("closed", reset.Status.StatusCode);
        Assert.Equal(0, reset.Status.ConsecutiveFailureCount); Assert.Equal(2, reset.Status.ResetAfterOutcomeSequence);
        Assert.Equal(1, (await Circuit(b.Workspace)).ConsecutiveFailureCount);
        await Outcome(a, 3, false); Assert.False((await Circuit(a.Workspace)).CanReset);
        await Outcome(a, 4, false); Assert.True((await Circuit(a.Workspace)).CanReset);
        Clock.Now = Clock.Now.AddHours(-1); // A clock adjustment cannot select an older reset.
        var next = await Reset(a.Workspace);
        Assert.Equal(next.Reset!.Id, (await Circuit(a.Workspace)).LastResetId);
        Assert.NotEqual(reset.Reset!.Id, next.Reset!.Id); Assert.Equal(4, next.Status.ResetAfterOutcomeSequence);
    }

    [Fact]
    public async Task HumanOutcomesSkippedDeferredAndUnsequencedFailuresAreIgnored()
    {
        var human = await Approved(); await Run(human);
        var failedHuman = human with { Job = await Another(human) };
        await Run(failedHuman); await Decide(failedHuman); await Run(failedHuman, (_, _) => TechnicalFailure());
        await Settings(human.Workspace, s => { s.OperatingModeCode = "automatic"; s.MaxConsecutiveFailures = 1; });
        var skipped = human with { Job = await Another(human) };
        await ChangeRule(human.Rule.Id, r => r.Enabled = false); await Run(skipped);
        await ChangeRule(human.Rule.Id, r => r.Enabled = true);
        await using (var db = Db())
        {
            var e = (await Executions(skipped.Job.Id)).Single();
            await db.AutomationExecutions.Where(x => x.Id == e.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.IsDeferred, true));
        }
        var status = await Circuit(human.Workspace);
        Assert.Equal("closed", status.StatusCode); Assert.Equal(0, status.ConsecutiveFailureCount);
        Assert.Null(status.LastAutomaticOutcomeSequence);
        Assert.Equal(1, (await Supervise(human.Workspace)).Executions.HumanEffectsLast24HoursCount);
    }
}
