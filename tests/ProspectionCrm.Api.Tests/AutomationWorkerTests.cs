using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ProspectionCrm.Api.Dtos.AutomationJobs;
using ProspectionCrm.Api.Entities;
using ProspectionCrm.Api.Services.Automation;
using Xunit;

namespace ProspectionCrm.Api.Tests;

public sealed class AutomationWorkerTests
{
    private sealed class State
    {
        public readonly Guid Workspace = Guid.NewGuid();
        public readonly Queue<AutomationJob> Jobs = new();
        public readonly List<TimeSpan> Delays = [];
        public readonly List<Guid> ProcessScopes = [];
        public int Finds, Recoveries, Processes, Disposed;
        public bool ThrowFirst;
        public void Add() => Jobs.Enqueue(new AutomationJob { WorkspaceId = Workspace, TriggerTypeCode = "manual",
            ActionCategoryCode = "general", StatusCode = "leased", LeaseOwner = Guid.NewGuid().ToString("N"), AttemptCount = 1 });
    }
    private sealed class Source(State state) : IAutomationWorkSource
    {
        public Task<IReadOnlyList<Guid>> FindAsync(Guid? after, int limit, bool expired, CancellationToken token)
        { state.Finds++; return Task.FromResult<IReadOnlyList<Guid>>(expired || state.Jobs.Count > 0 ? [state.Workspace] : []); }
    }
    private sealed class Recovery(State state) : IAutomationJobRecovery
    {
        public Task<int> RecoverAsync(Guid workspace, CancellationToken token)
        { Assert.Equal(state.Workspace, workspace); state.Recoveries++; return Task.FromResult(1); }
    }
    private sealed class Processor(State state) : IAutomationJobProcessor, IDisposable
    {
        private readonly Guid scopeId = Guid.NewGuid();
        public Task ProcessAsync(AutomationJob job, string leaseOwner, CancellationToken token)
        {
            Assert.Equal(job.LeaseOwner, leaseOwner); Assert.Equal(state.Workspace, job.WorkspaceId);
            state.ProcessScopes.Add(scopeId);
            if (++state.Processes == 1 && state.ThrowFirst) throw new InvalidOperationException("secret-marker");
            return Task.CompletedTask;
        }
        public void Dispose() => state.Disposed++;
    }
    private sealed class QueueStub(State state) : IAutomationJobQueue
    {
        public Task<AutomationJob?> ClaimAsync(Guid w, CancellationToken t) => Task.FromResult(state.Jobs.TryDequeue(out var job) ? job : null);
        public Task<AutomationJobResult<AutomationJob>> EnqueueAsync(Guid w, EnqueueAutomationJobRequest r, CancellationToken t) => throw new NotSupportedException();
        public Task<bool> CompleteAsync(Guid w, Guid j, string o, CancellationToken t) => throw new NotSupportedException();
        public Task<bool> FailAsync(Guid w, Guid j, string o, string? e, CancellationToken t) => throw new NotSupportedException();
        public Task<bool> ReleaseAsync(Guid w, Guid j, string o, DateTimeOffset? a, CancellationToken t) => throw new NotSupportedException();
        public Task<bool> CancelAsync(Guid w, Guid j, CancellationToken t) => throw new NotSupportedException();
        public Task<int> RecoverAsync(Guid w, CancellationToken t) => throw new NotSupportedException();
    }
    private sealed class Delay(State state, CancellationTokenSource stop) : IAutomationWorkerDelay
    {
        public Task WaitAsync(TimeSpan duration, CancellationToken token)
        {
            state.Delays.Add(duration);
            if (state.Jobs.Count == 0) { stop.Cancel(); token.ThrowIfCancellationRequested(); }
            return Task.CompletedTask;
        }
    }
    private static ServiceProvider Services(State state) => new ServiceCollection().AddSingleton(state)
        .AddScoped<IAutomationWorkSource, Source>().AddScoped<IAutomationJobRecovery, Recovery>()
        .AddScoped<IAutomationJobQueue, QueueStub>().AddScoped<IAutomationJobProcessor, Processor>().BuildServiceProvider();

    [Fact]
    public async Task DisabledWorkerDoesNotResolveWorkOrDelay()
    {
        var state = new State(); state.Add(); using var services = Services(state); using var stop = new CancellationTokenSource();
        using var worker = new AutomationWorker(services.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new AutomationWorkerOptions()), TimeProvider.System, new Delay(state, stop), NullLogger<AutomationWorker>.Instance);
        await worker.StartAsync(default); await worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, state.Finds); Assert.Equal(0, state.Processes); Assert.Empty(state.Delays);
    }

    [Fact]
    public async Task IdleCycleUsesCancellableDelayRatherThanSpinning()
    {
        var state = new State(); using var services = Services(state); using var stop = new CancellationTokenSource();
        using var worker = new AutomationWorker(services.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new AutomationWorkerOptions { Enabled = true }), TimeProvider.System,
            new Delay(state, stop), NullLogger<AutomationWorker>.Instance);
        await worker.StartAsync(stop.Token); await worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, state.Finds); Assert.Equal(0, state.Processes);
        Assert.Equal(TimeSpan.FromSeconds(5), Assert.Single(state.Delays));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecoveryClaimProcessingFreshScopesAndErrorDelay(bool throwFirst)
    {
        var state = new State { ThrowFirst = throwFirst }; state.Add(); state.Add();
        using var services = Services(state); using var stop = new CancellationTokenSource();
        using var worker = new AutomationWorker(services.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new AutomationWorkerOptions { Enabled = true }), TimeProvider.System,
            new Delay(state, stop), NullLogger<AutomationWorker>.Instance);
        await worker.StartAsync(stop.Token); await worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, state.Processes); Assert.Equal(2, state.Disposed);
        Assert.Equal(2, state.ProcessScopes.Distinct().Count()); Assert.Equal(1, state.Recoveries);
        Assert.Equal(throwFirst ? 2 : 1, state.Delays.Count);
        if (throwFirst) Assert.Equal(TimeSpan.FromSeconds(10), state.Delays[0]);
        Assert.Equal(TimeSpan.FromSeconds(5), state.Delays[^1]);
    }
}
