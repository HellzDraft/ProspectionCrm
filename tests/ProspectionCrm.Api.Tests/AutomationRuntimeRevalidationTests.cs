using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using ProspectionCrm.Api.Services.Automation;
using Xunit;

namespace ProspectionCrm.Api.Tests;

public sealed class AutomationRuntimeRevalidationTests(AutomationJobDatabase database)
    : AutomationRuntimeFixture(database), IClassFixture<AutomationJobDatabase>
{
    private sealed class AfterStart(Func<Task> change) : DbTransactionInterceptor
    {
        private int commits;
        public override async Task TransactionCommittedAsync(DbTransaction transaction,
            TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref commits) == 1) await change();
        }
    }

    [Theory]
    [InlineData("manual", "eligible-manual")]
    [InlineData("assist", "eligible-approval-required")]
    [InlineData("disabled-rule", "rule-disabled")]
    [InlineData("configuration", "task-created")]
    public async Task CurrentSettingsAndRuleWinAfterRunningWasPersisted(string change, string reason)
    {
        var setup = await Prepare(); var claim = await Claim(setup.Workspace);
        var interceptor = new AfterStart(async () =>
        {
            Assert.Equal("running", Assert.Single(await Executions(claim.Id)).StatusCode);
            if (change is "manual" or "assist") await Settings(setup.Workspace, s => s.OperatingModeCode = change);
            else if (change == "disabled-rule") await ChangeRule(setup.Rule.Id, r => r.Enabled = false);
            else await ChangeRule(setup.Rule.Id, r => r.ActionConfigurationJson = """{"title":"Updated after start"}""");
        });
        await Process(claim, interceptor: interceptor);
        Assert.Equal(reason, Assert.Single(await Executions(claim.Id)).ReasonCode);
        Assert.Equal("completed", (await Job(claim.Id)).StatusCode);
        if (change == "configuration") Assert.Equal("Updated after start", Assert.Single(await Tasks(claim.Id)).Title);
        else Assert.Empty(await Tasks(claim.Id));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LateKillSwitchOrQuotaDeferralCanBeRetriedInsteadOfMistakenForCompletion(bool quota)
    {
        var setup = await Prepare(); var claim = await Claim(setup.Workspace);
        await Settings(setup.Workspace, s => s.MaxExecutionsPerMinute = 1);
        var interceptor = new AfterStart(async () =>
        {
            Assert.Equal("running", Assert.Single(await Executions(claim.Id)).StatusCode);
            if (quota) { await Another(setup); await Process(await Claim(setup.Workspace)); }
            else await Settings(setup.Workspace, s => s.IsEnabled = false);
        });
        await Process(claim, interceptor: interceptor);
        var deferred = Assert.Single(await Executions(claim.Id));
        Assert.Equal("skipped", deferred.StatusCode); Assert.True(deferred.IsDeferred);
        Assert.Equal(quota ? "minute-quota" : "automation-disabled", deferred.ReasonCode);
        Assert.Equal("pending", (await Job(claim.Id)).StatusCode); Assert.Empty(await Tasks(claim.Id));
        Clock.Now = Clock.Now.AddMinutes(1);
        await Settings(setup.Workspace, s => s.IsEnabled = true); await Available(claim.Id);
        await Process(await Claim(setup.Workspace));
        Assert.Single(await Tasks(claim.Id)); Assert.Equal("completed", (await Job(claim.Id)).StatusCode);
        Assert.Equal(2, (await Executions(claim.Id)).Count);
    }
}
