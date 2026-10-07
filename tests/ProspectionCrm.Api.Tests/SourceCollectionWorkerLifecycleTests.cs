using System.Collections.Concurrent;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProspectionCrm.Api.Services.Collection;
using Xunit;

namespace ProspectionCrm.Api.Tests;

public sealed class SourceCollectionWorkerLifecycleTests
{
    private sealed class Probe
    {
        public int Calls;
        public readonly ConcurrentBag<Guid> Disposed = new();
        public readonly TaskCompletionSource Recovered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private sealed class Processor(Probe probe) : ISourceCollectionJobProcessor, IDisposable
    {
        private readonly Guid scopeId = Guid.NewGuid();
        public Task<bool> ProcessNextAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (Interlocked.Increment(ref probe.Calls) == 1) throw new InvalidOperationException("Unexpected claim failure");
            probe.Recovered.TrySetResult(); return Task.FromResult(false);
        }
        public void Dispose() => probe.Disposed.Add(scopeId);
    }
    private sealed class Logger : ILogger<SourceCollectionWorker>
    {
        public readonly ConcurrentQueue<Exception> Errors = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        { if (exception is not null) Errors.Enqueue(exception); }
    }

    [Fact]
    public async Task UnexpectedCycleFailureIsLoggedScopesAreDisposedAndIdleWaitIsCancellable()
    {
        var probe = new Probe(); var logger = new Logger();
        await using var provider = new ServiceCollection().AddSingleton(probe)
            .AddScoped<ISourceCollectionJobProcessor, Processor>().BuildServiceProvider(validateScopes: true);
        using var worker = new SourceCollectionWorker(provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new SourceCollectionWorkerOptions { Enabled = true, IdleDelaySeconds = 1 }), logger);
        await worker.StartAsync(default); await probe.Recovered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await worker.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, probe.Calls); Assert.Equal(2, probe.Disposed.Distinct().Count());
        Assert.IsType<InvalidOperationException>(Assert.Single(logger.Errors));
    }

    [Fact]
    public async Task DisabledWorkerNeverResolvesProcessor()
    {
        await using var provider = new ServiceCollection().BuildServiceProvider(validateScopes: true);
        using var worker = new SourceCollectionWorker(provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new SourceCollectionWorkerOptions()), new Logger());
        await worker.StartAsync(default); await worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5));
        await worker.StopAsync(default);
    }

    [Theory]
    [InlineData("IdleDelaySeconds", "0")]
    [InlineData("IdleDelaySeconds", "301")]
    [InlineData("LeaseDurationSeconds", "29")]
    [InlineData("LeaseDurationSeconds", "3601")]
    public void InvalidOptionsFailAtApplicationStartup(string key, string value)
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseEnvironment("Testing")
            .UseSetting($"SourceCollectionWorker:{key}", value));
        Assert.Throws<OptionsValidationException>(() => factory.CreateClient());
    }
}
