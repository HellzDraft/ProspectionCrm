using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using ProspectionCrm.Api.Data;
using ProspectionCrm.Api.Dtos.CollectionJobs;
using ProspectionCrm.Api.Entities;
using ProspectionCrm.Api.Services;
using ProspectionCrm.Api.Services.Collection;
using Xunit;

namespace ProspectionCrm.Api.Tests;

public sealed class SourceCollectionWorkerTests : PersistentSourceIdentityFixture
{
    private const string Url = "https://feeds.example.org/jobs.xml";
    private sealed class Transport : IRssFeedTransport
    {
        public readonly ConcurrentQueue<Uri> Calls = new();
        public Func<Uri, CancellationToken, Task>? Before;
        public async Task<RssFeedResponse> FetchAsync(Uri uri, CancellationToken token)
        {
            Calls.Enqueue(uri); if (Before is not null) await Before(uri, token);
            return new(Encoding.UTF8.GetBytes(RssAtomFeedParserTests.Rss(
                "<item><title>Developer</title><guid>one</guid><link>https://jobs.example.org/one</link></item>")), uri);
        }
    }
    private sealed class ForbiddenWorkspace : ICurrentWorkspaceProvider
    {
        public Task<Guid> GetCurrentWorkspaceIdAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Worker must use the persisted workspace");
    }
    private readonly Transport transport = new();
    private WebApplicationFactory<Program> Factory(bool enabled = false, bool forbidWorkspace = false, int leaseSeconds = 300,
        IInterceptor? interceptor = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseEnvironment("Testing")
            .UseSetting("ConnectionStrings:DefaultConnection", Postgres.GetConnectionString())
            .UseSetting("SourceCollectionWorker:Enabled", enabled.ToString())
            .UseSetting("SourceCollectionWorker:MaxAttempts", "1")
            .UseSetting("SourceCollectionWorker:IdleDelaySeconds", "1")
            .UseSetting("SourceCollectionWorker:LeaseDurationSeconds", leaseSeconds.ToString())
            .ConfigureServices(services =>
            {
                services.RemoveAll<IRssFeedTransport>(); services.AddSingleton<IRssFeedTransport>(transport);
                if (interceptor is not null) services.AddDbContext<ProspectionCrmDbContext>(o => o.AddInterceptors(interceptor));
                if (forbidWorkspace) { services.RemoveAll<ICurrentWorkspaceProvider>(); services.AddScoped<ICurrentWorkspaceProvider, ForbiddenWorkspace>(); }
            }));
    private static HttpClient Client(WebApplicationFactory<Program> factory) => factory.CreateClient(new() { BaseAddress = new("https://localhost") });
    private async Task<Setup> PrepareRss()
    {
        var setup = await PrepareAsync(); await using var db = Db();
        await db.Opportunities.ExecuteDeleteAsync(); await db.SourceExecutions.ExecuteDeleteAsync();
        await db.SourceConfigurations.ExecuteUpdateAsync(s => s.SetProperty(x => x.SourceTypeCode, "rss"));
        await db.SavedSearches.ExecuteUpdateAsync(s => s.SetProperty(x => x.SearchUrl, Url));
        return setup;
    }
    private async Task<Guid> Enqueue(Setup setup)
    {
        using var factory = Factory(); using var client = Client(factory);
        using var response = await client.PostAsJsonAsync($"/api/saved-searches/{setup.Search}/collection-jobs", new { pipelineStageId = setup.Stage });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<SourceCollectionJobDto>())!.Id;
    }
    private static async Task<bool> Process(WebApplicationFactory<Program> factory, CancellationToken token = default)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ISourceCollectionJobProcessor>().ProcessNextAsync(token);
    }
    private async Task<SourceCollectionJob> Read(Guid id)
    { await using var db = Db(); return await db.SourceCollectionJobs.AsNoTracking().SingleAsync(x => x.Id == id); }
    private async Task<SourceCollectionJob> Terminal(Guid id)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (true)
        {
            timeout.Token.ThrowIfCancellationRequested(); var job = await Read(id);
            if (job.StatusCode is "succeeded" or "failed") return job;
            await Task.Delay(25, timeout.Token);
        }
    }
    private static void Finished(SourceCollectionJob job, string status, string? code = null)
    {
        Assert.Equal(status, job.StatusCode); Assert.Equal(code, job.ErrorCode); Assert.Equal(1, job.AttemptCount);
        Assert.NotNull(job.StartedAt); Assert.NotNull(job.FinishedAt); Assert.Null(job.LeaseToken); Assert.Null(job.LeaseExpiresAt);
    }

    [Fact]
    public async Task RestartConsumesPersistedJobAndSecondAttemptUsesExistingIngestionDeduplication()
    {
        var setup = await PrepareRss(); var id = await Enqueue(setup);
        Assert.Empty(transport.Calls); Assert.Equal("queued", (await Read(id)).StatusCode);
        using (var restarted = Factory(enabled: true))
        using (var client = Client(restarted))
        {
            var job = await Terminal(id); Finished(job, "succeeded"); Assert.NotNull(job.SourceExecutionId);
            using var history = await client.GetAsync($"/api/source-executions/{job.SourceExecutionId}/history");
            Assert.Equal(HttpStatusCode.OK, history.StatusCode);
        }
        var second = await Enqueue(setup);
        using (var restarted = Factory(enabled: true))
        using (var client = Client(restarted)) Finished(await Terminal(second), "succeeded");
        await using var db = Db(); Assert.Equal(2, await db.SourceExecutions.CountAsync());
        Assert.Single(await db.Opportunities.ToListAsync()); Assert.Single(await db.OpportunitySources.ToListAsync());
        Assert.Equal(1, await db.SourceExecutions.SumAsync(x => x.ItemsCreated));
        Assert.Equal(1, await db.SourceExecutions.SumAsync(x => x.ItemsUpdated)); Assert.Equal(2, transport.Calls.Count);
        Assert.Equal(2, await db.SourceExecutionItems.CountAsync());
    }

    [Fact]
    public async Task TwoHostedWorkersCannotFetchSameJobAndRunningCancellationConflicts()
    {
        var setup = await PrepareRss(); var id = await Enqueue(setup);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        transport.Before = async (_, token) => { entered.TrySetResult(); await release.Task.WaitAsync(token); };
        using var first = Factory(enabled: true); using var client = Client(first);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        using var second = Factory(enabled: true); using var secondClient = Client(second);
        try
        {
            Assert.False(await Process(second));
            using var cancel = await secondClient.PostAsync($"/api/source-collection-jobs/{id}/cancel", null);
            Assert.Equal(HttpStatusCode.Conflict, cancel.StatusCode);
            // A database transaction can lock the running row while the network waits.
            await using var db = Db(); await using var tx = await db.Database.BeginTransactionAsync();
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"SourceCollectionJobs\" WHERE \"Id\" = {id} FOR UPDATE NOWAIT");
            Assert.Single(transport.Calls); await tx.CommitAsync();
        }
        finally { release.TrySetResult(); }
        Finished(await Terminal(id), "succeeded"); Assert.Single(transport.Calls);
        await using var check = Db(); Assert.Single(await check.SourceExecutions.ToListAsync()); Assert.Single(await check.Opportunities.ToListAsync());
    }

    [Fact]
    public async Task CancellationAndClaimRaceHasExactlyOneWinner()
    {
        var setup = await PrepareRss(); var id = await Enqueue(setup);
        using var factory = Factory(); using var client = Client(factory);
        await using var scope = factory.Services.CreateAsyncScope();
        var queue = scope.ServiceProvider.GetRequiredService<ISourceCollectionJobQueue>();
        var cancelled = client.PostAsync($"/api/source-collection-jobs/{id}/cancel", null);
        var claimed = queue.ClaimAsync(default);
        using var response = await cancelled; var job = await claimed;
        if (job is null) { Assert.Equal(HttpStatusCode.NoContent, response.StatusCode); Assert.Equal("cancelled", (await Read(id)).StatusCode); }
        else { Assert.Equal(HttpStatusCode.Conflict, response.StatusCode); Assert.Equal("running", (await Read(id)).StatusCode); Assert.Equal(1, job.AttemptCount); }
        Assert.Empty(transport.Calls);
    }

    [Theory]
    [InlineData("workspace", "WorkspaceUnavailable")]
    [InlineData("search", "InactiveResource")]
    [InlineData("source", "InactiveResource")]
    [InlineData("pipeline", "InactiveResource")]
    [InlineData("stage", "InactiveResource")]
    [InlineData("url", "UnsafeFeedUrl")]
    [InlineData("criteria", "InvalidRssCriteria")]
    [InlineData("adapter", "UnsupportedSourceType")]
    public async Task RevalidatesCurrentConfigurationBeforeNetwork(string change, string code)
    {
        var setup = await PrepareRss(); var id = await Enqueue(setup); await using var db = Db();
        switch (change)
        {
            case "workspace": await db.Workspaces.ExecuteUpdateAsync(s => s.SetProperty(x => x.ArchivedAt, DateTimeOffset.UtcNow)); break;
            case "search": await db.SavedSearches.ExecuteUpdateAsync(s => s.SetProperty(x => x.Enabled, false)); break;
            case "source": await db.SourceConfigurations.ExecuteUpdateAsync(s => s.SetProperty(x => x.ArchivedAt, DateTimeOffset.UtcNow)); break;
            case "pipeline": await db.Pipelines.ExecuteUpdateAsync(s => s.SetProperty(x => x.ArchivedAt, DateTimeOffset.UtcNow)); break;
            case "stage": await db.PipelineStages.ExecuteUpdateAsync(s => s.SetProperty(x => x.ArchivedAt, DateTimeOffset.UtcNow)); break;
            case "url": await db.SavedSearches.ExecuteUpdateAsync(s => s.SetProperty(x => x.SearchUrl, "http://localhost/private")); break;
            case "criteria": await db.SavedSearches.ExecuteUpdateAsync(s => s.SetProperty(x => x.CriteriaJson, "{\"maxItems\":101}")); break;
            case "adapter": await db.SourceConfigurations.ExecuteUpdateAsync(s => s.SetProperty(x => x.SourceTypeCode, "other")); break;
        }
        using var factory = Factory(forbidWorkspace: true); Assert.True(await Process(factory));
        var job = await Read(id); Finished(job, "failed", code); Assert.Null(job.SourceExecutionId);
        Assert.False(await Process(factory)); Assert.Empty(transport.Calls); Assert.Empty(await db.SourceExecutions.ToListAsync());
    }

    [Fact]
    public async Task UsesCurrentUrlAndPersistedWorkspaceWithMultipleActiveWorkspaces()
    {
        var setup = await PrepareRss(); var id = await Enqueue(setup); await using var db = Db();
        var other = await SeedAsync(db);
        await db.SavedSearches.Where(x => x.Id == setup.Search).ExecuteUpdateAsync(s => s.SetProperty(x => x.SearchUrl, "https://feeds.example.org/new.xml"));
        using var factory = Factory(forbidWorkspace: true); Assert.True(await Process(factory));
        var job = await Read(id); Finished(job, "succeeded");
        Assert.Equal("/new.xml", Assert.Single(transport.Calls).AbsolutePath);
        Assert.Equal(setup.Workspace, (await db.SourceExecutions.SingleAsync(x => x.Id == job.SourceExecutionId)).WorkspaceId);
        Assert.Single(await db.Opportunities.Where(x => x.WorkspaceId == setup.Workspace).ToListAsync());
        Assert.Equal(other.Opportunity, (await db.Opportunities.SingleAsync(x => x.WorkspaceId == other.Workspace)).Id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NetworkFailurePreservesHistoryAndHostedWorkerContinuesInNewScope(bool unexpected)
    {
        var setup = await PrepareRss(); var first = await Enqueue(setup);
        await using (var db = Db())
        {
            var stage = new PipelineStage { PipelineId = setup.Pipeline, Name = "Second", CategoryCode = "active", SortOrder = 1 };
            db.Add(stage); await db.SaveChangesAsync(); setup = setup with { Stage = stage.Id };
        }
        var second = await Enqueue(setup);
        var code = unexpected ? "CollectionInternalError" : "UpstreamTimeout";
        transport.Before = (_, _) => transport.Calls.Count == 1
            ? Task.FromException(unexpected ? new InvalidOperationException("private exception text")
                : new SourceCollectionException(new("UpstreamTimeout", 504))) : Task.CompletedTask;
        using var factory = Factory(enabled: true); using var client = Client(factory);
        var failed = await Terminal(first); Finished(failed, "failed", code);
        Finished(await Terminal(second), "succeeded");
        await using var check = Db(); var execution = await check.SourceExecutions.SingleAsync(x => x.Id == failed.SourceExecutionId);
        Assert.Equal("failed", execution.StatusCode); Assert.Equal(code, execution.ErrorMessage);
        Assert.Equal(2, transport.Calls.Count); Assert.Equal(2, await check.SourceExecutions.CountAsync());
        Assert.Single(await check.Opportunities.ToListAsync()); Assert.False(await Process(factory));
        await using var a = factory.Services.CreateAsyncScope(); await using var b = factory.Services.CreateAsyncScope();
        Assert.NotSame(a.ServiceProvider.GetRequiredService<ProspectionCrmDbContext>(), b.ServiceProvider.GetRequiredService<ProspectionCrmDbContext>());
    }

    [Fact]
    public async Task HostStopCancelsNetworkPreservesExecutionAndNeverRequeues()
    {
        var setup = await PrepareRss(); var id = await Enqueue(setup);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        transport.Before = async (_, token) => { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, token); };
        using var factory = Factory(enabled: true); using var client = Client(factory);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        var worker = Assert.Single(factory.Services.GetServices<IHostedService>().OfType<SourceCollectionWorker>());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20)); await worker.StopAsync(timeout.Token);
        var job = await Read(id); Finished(job, "failed", "CollectionWorkerStopping"); Assert.NotNull(job.SourceExecutionId);
        await using var db = Db(); Assert.Equal("cancelled", (await db.SourceExecutions.SingleAsync()).StatusCode);
        Assert.Empty(await db.Opportunities.ToListAsync()); Assert.False(await Process(factory)); Assert.Single(transport.Calls);
    }

    [Fact]
    public async Task LeaseDeadlineCancelsCooperativelyAndFinalizesWithoutRetry()
    {
        var setup = await PrepareRss(); var id = await Enqueue(setup);
        transport.Before = (_, token) => Task.Delay(Timeout.Infinite, token);
        using var factory = Factory(leaseSeconds: 30);
        Assert.True(await Process(factory).WaitAsync(TimeSpan.FromSeconds(50)));
        var job = await Read(id); Finished(job, "failed", "CollectionLeaseExpired"); Assert.NotNull(job.SourceExecutionId);
        await using var db = Db(); Assert.Equal("cancelled", (await db.SourceExecutions.SingleAsync()).StatusCode);
        Assert.Empty(await db.Opportunities.ToListAsync()); Assert.False(await Process(factory)); Assert.Single(transport.Calls);
    }

    [Fact]
    public async Task BusinessPersistenceFailureRollsBackBusinessButCommitsHistoryAndJobLink()
    {
        var setup = await PrepareRss(); var id = await Enqueue(setup);
        using var factory = Factory(interceptor: new BusinessFailure()); Assert.True(await Process(factory));
        var job = await Read(id); Finished(job, "failed", "PersistenceFailure"); Assert.NotNull(job.SourceExecutionId);
        await using var db = Db(); Assert.Empty(await db.Opportunities.ToListAsync()); Assert.Empty(await db.OpportunitySources.ToListAsync());
        Assert.Equal(job.SourceExecutionId, (await db.SourceExecutions.SingleAsync()).Id);
        Assert.Equal("failed", (await db.SourceExecutions.SingleAsync()).StatusCode);
        Assert.Single(await db.SourceExecutionItems.ToListAsync()); Assert.False(await Process(factory));
    }

    private sealed class BusinessFailure : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (data.Context!.ChangeTracker.Entries<Opportunity>().Any(x => x.State == EntityState.Added))
                throw new InvalidOperationException("Private persistence diagnostics must not become ErrorCode");
            return ValueTask.FromResult(result);
        }
    }

    [Fact]
    public async Task ConfigurationMutationDuringFetchCannotCreateBusinessRows()
    {
        var setup = await PrepareRss(); var id = await Enqueue(setup);
        transport.Before = async (_, _) =>
        {
            await using var db = Db(); await db.SavedSearches.ExecuteUpdateAsync(s => s.SetProperty(x => x.SearchUrl, "https://feeds.example.org/changed.xml"));
        };
        using var factory = Factory(); Assert.True(await Process(factory));
        var job = await Read(id); Assert.Equal("failed", job.StatusCode); Assert.NotNull(job.ErrorCode); Assert.NotNull(job.SourceExecutionId);
        await using var check = Db(); Assert.Empty(await check.Opportunities.ToListAsync()); Assert.Empty(await check.OpportunitySources.ToListAsync());
        Assert.Equal("failed", (await check.SourceExecutions.SingleAsync()).StatusCode);
    }
}
