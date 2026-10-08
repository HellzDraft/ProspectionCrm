using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using ProspectionCrm.Api.Services;
using ProspectionCrm.Api.Services.Automation;
using Xunit;

namespace ProspectionCrm.Api.Tests;

public sealed class AutomationWorkerIntegrationTests(AutomationJobDatabase database)
    : AutomationRuntimeFixture(database), IClassFixture<AutomationJobDatabase>
{
    private sealed class ForbiddenWorkspace : ICurrentWorkspaceProvider
    {
        public Task<Guid> GetCurrentWorkspaceIdAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Worker must never resolve the HTTP workspace.");
    }

    [Fact]
    public async Task HostedWorkerRecoversAndProcessesMultipleWorkspacesWithoutCurrentWorkspaceProvider()
    {
        var a = await Prepare(); var b = await Prepare();
        var old = await Claim(a.Workspace); await Expire(old.Id);
        using var factory = Factory(null).WithWebHostBuilder(builder => builder
            .UseSetting("AutomationWorker:Enabled", "true").UseSetting("AutomationWorker:IdleDelaySeconds", "1")
            .ConfigureServices(s => {
                s.RemoveAll<ICurrentWorkspaceProvider>(); s.AddScoped<ICurrentWorkspaceProvider, ForbiddenWorkspace>();
                s.RemoveAll<TimeProvider>(); s.AddSingleton<TimeProvider>(Clock);
            }));
        using var client = Client(factory);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while ((await Job(a.Job.Id)).StatusCode != "completed" || (await Job(b.Job.Id)).StatusCode != "completed")
            await Task.Delay(20, timeout.Token);
        Assert.Single(await Tasks(a.Job.Id)); Assert.Single(await Tasks(b.Job.Id));
        Assert.Equal(2, (await Job(a.Job.Id)).AttemptCount);
    }

    [Theory]
    [InlineData("IdleDelaySeconds", "0")]
    [InlineData("ErrorDelaySeconds", "0")]
    [InlineData("RecoveryIntervalSeconds", "0")]
    [InlineData("DeferredDelaySeconds", "0")]
    [InlineData("MaxAttempts", "11")]
    [InlineData("WorkspaceBatchSize", "101")]
    [InlineData("InitialRetryDelaySeconds", "0")]
    [InlineData("MaxRetryDelaySeconds", "1")]
    public void InvalidOptionsFailOnHostStartupEvenWhenDisabled(string key, string value)
    {
        using var factory = Factory(null).WithWebHostBuilder(b => b.UseSetting("AutomationWorker:" + key, value));
        Assert.Throws<OptionsValidationException>(() => Client(factory));
    }

    [Fact]
    public async Task WorkDiscoveryIsBoundedDeterministicAndSeparatesReadyFromExpired()
    {
        var a = await Prepare(); var b = await Prepare();
        await using var db = Db(); var source = new AutomationWorkSource(db);
        var all = await source.FindAsync(null, 100, false, default);
        Assert.Contains(a.Workspace, all); Assert.Contains(b.Workspace, all);
        var first = await source.FindAsync(null, 1, false, default); Assert.Single(first);
        var later = await source.FindAsync(first[0], 100, false, default);
        Assert.DoesNotContain(first[0], later);
        var claim = await Claim(a.Workspace); await Expire(claim.Id);
        Assert.DoesNotContain(a.Workspace, await source.FindAsync(null, 100, false, default));
        Assert.Contains(a.Workspace, await source.FindAsync(null, 100, true, default));
        await Recover(a.Workspace);
        Assert.Contains(a.Workspace, await source.FindAsync(null, 100, false, default));
    }
}
