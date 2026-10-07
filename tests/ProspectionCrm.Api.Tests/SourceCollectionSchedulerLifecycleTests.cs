using System.Collections.Concurrent;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProspectionCrm.Api.Services.Collection;
using Xunit;

namespace ProspectionCrm.Api.Tests;

public sealed class SourceCollectionSchedulerLifecycleTests
{
    private sealed class Probe
    {
        public int Calls;
        public readonly ConcurrentBag<Guid> Disposed = new();
        public readonly TaskCompletionSource Recovered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private sealed class Processor(Probe probe) : ISourceCollectionSchedulingService, IDisposable
    {
        private readonly Guid scopeId = Guid.NewGuid();
        public Task<int> ScheduleDueAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (Interlocked.Increment(ref probe.Calls) == 1) throw new InvalidOperationException("Unexpected claim failure");
            probe.Recovered.TrySetResult(); return Task.FromResult(0);
        }
        public void Dispose() => probe.Disposed.Add(scopeId);
    }
    private sealed class Logger : ILogger<SourceCollectionScheduler>
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
            .AddScoped<ISourceCollectionSchedulingService, Processor>().BuildServiceProvider(validateScopes: true);
        using var worker = new SourceCollectionScheduler(provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new SourceCollectionSchedulerOptions { Enabled = true, PollIntervalSeconds = 1 }), logger);
        await worker.StartAsync(default); await probe.Recovered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await worker.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, probe.Calls); Assert.Equal(2, probe.Disposed.Distinct().Count());
        Assert.IsType<InvalidOperationException>(Assert.Single(logger.Errors));
    }

    [Fact]
    public async Task DisabledSchedulerNeverResolvesProcessor()
    {
        await using var provider = new ServiceCollection().BuildServiceProvider(validateScopes: true);
        using var worker = new SourceCollectionScheduler(provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new SourceCollectionSchedulerOptions()), new Logger());
        await worker.StartAsync(default); await worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5));
        await worker.StopAsync(default);
    }

    [Theory]
    [InlineData("PollIntervalSeconds", "0")]
    [InlineData("PollIntervalSeconds", "3601")]
    [InlineData("BatchSize", "0")]
    [InlineData("BatchSize", "501")]
    public void InvalidOptionsFailAtApplicationStartup(string key, string value)
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseEnvironment("Testing")
            .UseSetting($"SourceCollectionScheduler:{key}", value));
        Assert.Throws<OptionsValidationException>(() => factory.CreateClient());
    }
}
