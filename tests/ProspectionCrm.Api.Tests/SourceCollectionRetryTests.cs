using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ProspectionCrm.Api.Data;
using ProspectionCrm.Api.Dtos.CollectionJobs;
using ProspectionCrm.Api.Entities;
using ProspectionCrm.Api.Services.Collection;
using Xunit;

namespace ProspectionCrm.Api.Tests;

public sealed class SourceCollectionRetryTests : PersistentSourceIdentityFixture
{
    private sealed class Feed : IRssFeedTransport
    {
        public int Calls;
        public int Failures = 1;
        public SourceAdapterError Error = new("UpstreamHttpError", 502, 503);
        public Func<CancellationToken, Task>? Gate;
        public async Task<RssFeedResponse> FetchAsync(Uri uri, CancellationToken token)
        {
            var number = Interlocked.Increment(ref Calls);
            if (Gate is not null) await Gate(token);
            if (number <= Failures) throw new SourceCollectionException(Error);
            return new(Encoding.UTF8.GetBytes(RssAtomFeedParserTests.Rss("<item><title>Developer</title><guid>one</guid></item>")), uri);
        }
    }
    private readonly Feed feed = new();
    private WebApplicationFactory<Program> Factory(int max = 3, bool enabled = false, int delay = 60) => new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        b.UseEnvironment("Testing").UseSetting("ConnectionStrings:DefaultConnection", Postgres.GetConnectionString())
            .UseSetting("SourceCollectionWorker:Enabled", enabled.ToString()).UseSetting("SourceCollectionWorker:MaxAttempts", max.ToString())
            .UseSetting("SourceCollectionWorker:IdleDelaySeconds", "1").UseSetting("SourceCollectionWorker:InitialRetryDelaySeconds", delay.ToString())
            .ConfigureServices(s => { s.RemoveAll<IRssFeedTransport>(); s.AddSingleton<IRssFeedTransport>(feed); }));
    private async Task<(Setup Setup, Guid Id)> Enqueue()
    {
        var setup = await PrepareAsync(); await using var db = Db();
        await db.Opportunities.ExecuteDeleteAsync(); await db.SourceExecutions.ExecuteDeleteAsync();
        await db.SourceConfigurations.ExecuteUpdateAsync(s => s.SetProperty(x => x.SourceTypeCode, "rss"));
        await db.SavedSearches.ExecuteUpdateAsync(s => s.SetProperty(x => x.SearchUrl, "https://feeds.example.org/jobs.xml"));
        using var factory = Factory(); using var client = factory.CreateClient(new() { BaseAddress = new("https://localhost") });
        using var response = await client.PostAsJsonAsync($"/api/saved-searches/{setup.Search}/collection-jobs", new { pipelineStageId = setup.Stage });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return (setup, (await response.Content.ReadFromJsonAsync<SourceCollectionJobDto>())!.Id);
    }
    private async Task<SourceCollectionJob> Job(Guid id)
    { await using var db = Db(); return await db.SourceCollectionJobs.AsNoTracking().SingleAsync(x => x.Id == id); }
    private static async Task<bool> Process(WebApplicationFactory<Program> f)
    { await using var scope = f.Services.CreateAsyncScope(); return await scope.ServiceProvider.GetRequiredService<ISourceCollectionJobProcessor>().ProcessNextAsync(default); }
    private static async Task<int> Recover(WebApplicationFactory<Program> f)
    { await using var scope = f.Services.CreateAsyncScope(); return await scope.ServiceProvider.GetRequiredService<ISourceCollectionJobRecovery>().RecoverAsync(default); }
    private static async Task<SourceCollectionJob> Claim(WebApplicationFactory<Program> f)
    { await using var scope = f.Services.CreateAsyncScope(); return (await scope.ServiceProvider.GetRequiredService<ISourceCollectionJobQueue>().ClaimAsync(default))!; }
    private async Task Available(Guid id)
    { await using var db = Db(); await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE \"SourceCollectionJobs\" SET \"AvailableAt\" = statement_timestamp() WHERE \"Id\" = {id}"); }
    private async Task Expire(SourceCollectionJob job)
    {
        await using var db = Db(); var expiry = job.StartedAt!.Value.AddTicks(10);
        await db.SourceCollectionJobs.Where(x => x.Id == job.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.LeaseExpiresAt, expiry));
    }
    private async Task<Guid> Attach(Setup setup, SourceCollectionJob job, string status, string? code = null, int? http = null)
    {
        await using var db = Db(); await using var tx = await db.Database.BeginTransactionAsync();
        var execution = new SourceExecution { WorkspaceId = setup.Workspace, SavedSearchId = setup.Search, SourceConfigurationId = setup.Configuration,
            StatusCode = status, TriggerTypeCode = "manual", StartedAt = job.StartedAt!.Value,
            FinishedAt = status == "running" ? null : DateTimeOffset.UtcNow, ErrorMessage = code };
        db.Add(execution); await db.SaveChangesAsync();
        await new CollectionJobAttempt(job.Id, job.LeaseToken!.Value, execution.Id, "manual").AttachExecutionAsync(db, setup.Workspace, default, http, code);
        await tx.CommitAsync(); return execution.Id;
    }

    [Fact]
    public async Task HostedWorkerRetriesAutomaticallyUsingFreshScopes()
    {
        var (_, id) = await Enqueue(); using var factory = Factory(enabled: true, delay: 1);
        using var client = factory.CreateClient(new() { BaseAddress = new("https://localhost") });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        SourceCollectionJob job;
        do { await Task.Delay(25, timeout.Token); job = await Job(id); } while (job.StatusCode != "succeeded");
        Assert.Equal(2, job.AttemptCount); Assert.Equal(2, feed.Calls);
        await using var db = Db(); Assert.Equal(2, await db.SourceCollectionJobAttempts.CountAsync());
        Assert.Equal(2, await db.SourceExecutions.CountAsync()); Assert.Single(await db.Opportunities.ToListAsync());
    }

    [Fact]
    public async Task AbandonedLastAttemptTerminatesWithoutAnotherClaim()
    {
        var (_, id) = await Enqueue(); using var factory = Factory(max: 1); var claimed = await Claim(factory);
        await Expire(claimed); Assert.Equal(1, await Recover(factory));
        var job = await Job(id); Assert.Equal("failed", job.StatusCode); Assert.Equal("CollectionAbandoned", job.ErrorCode);
        Assert.Equal(1, job.AttemptCount); Assert.False(await Process(factory)); Assert.Equal(0, feed.Calls);
    }

    [Fact]
    public async Task RetryBackoffAndRestartKeepBothExecutionsThenStopAfterSuccess()
    {
        var (_, id) = await Enqueue();
        using (var first = Factory()) Assert.True(await Process(first));
        var queued = await Job(id); Assert.Equal("queued", queued.StatusCode); Assert.Equal(1, queued.AttemptCount);
        Assert.Equal("manual", queued.TriggerTypeCode); Assert.Null(queued.StartedAt); Assert.Null(queued.FinishedAt);
        Assert.Null(queued.SourceExecutionId); Assert.Null(queued.ErrorCode); Assert.Null(queued.LeaseToken); Assert.Null(queued.LeaseExpiresAt);
        await using var db = Db(); var attempt = await db.SourceCollectionJobAttempts.AsNoTracking().SingleAsync();
        Assert.Equal("failed", attempt.StatusCode); Assert.Equal("UpstreamHttpError", attempt.ErrorCode); Assert.Equal(503, attempt.UpstreamStatusCode);
        Assert.NotNull(attempt.SourceExecutionId); Assert.InRange(queued.AvailableAt - attempt.FinishedAt!.Value, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(65));
        using var restarted = Factory(); Assert.False(await Process(restarted)); Assert.Equal(1, feed.Calls);
        Assert.Equal(queued.AvailableAt, (await Job(id)).AvailableAt);
        await Available(id); Assert.True(await Process(restarted)); var success = await Job(id);
        Assert.Equal("succeeded", success.StatusCode); Assert.Equal(2, success.AttemptCount); Assert.Null(success.ErrorCode);
        Assert.Equal("manual", success.TriggerTypeCode); Assert.False(await Process(restarted)); Assert.Equal(2, feed.Calls);
        var attempts = await db.SourceCollectionJobAttempts.AsNoTracking().OrderBy(x => x.AttemptNumber).ToArrayAsync();
        Assert.Equal(new[] { 1, 2 }, attempts.Select(x => x.AttemptNumber)); Assert.Equal(new[] { "failed", "succeeded" }, attempts.Select(x => x.StatusCode));
        Assert.Equal(success.SourceExecutionId, attempts[1].SourceExecutionId);
        Assert.Equal(2, await db.SourceExecutions.CountAsync()); Assert.Single(await db.Opportunities.ToListAsync());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task ExactlyMaxAttemptsAndNoAdditionalClaim(int max)
    {
        var (_, id) = await Enqueue(); feed.Failures = 100; using var factory = Factory(max);
        for (var number = 1; number <= max; number++)
        {
            Assert.True(await Process(factory)); var job = await Job(id); Assert.Equal(number, job.AttemptCount);
            Assert.Equal(number == max ? "failed" : "queued", job.StatusCode);
            Assert.False(await Process(factory));
            if (number < max) await Available(id);
        }
        Assert.Equal(max, feed.Calls); await using var db = Db(); Assert.Equal(max, await db.SourceExecutions.CountAsync());
        Assert.Equal(max, await db.SourceCollectionJobAttempts.CountAsync()); Assert.Empty(await db.Opportunities.ToListAsync());
        Assert.Equal("UpstreamHttpError", (await Job(id)).ErrorCode);
    }

    [Fact]
    public async Task LoweredMaxAttemptsDoesNotStartAnotherAttemptAlreadyInBackoff()
    {
        var (_, id) = await Enqueue(); using (var first = Factory()) Assert.True(await Process(first));
        await Available(id); using var restarted = Factory(max: 1); Assert.True(await Process(restarted));
        var job = await Job(id); Assert.Equal("failed", job.StatusCode); Assert.Equal("CollectionAttemptsExhausted", job.ErrorCode);
        Assert.Equal(1, job.AttemptCount); Assert.Equal(1, feed.Calls); Assert.False(await Process(restarted));
    }

    [Fact]
    public async Task InvalidConfigurationEndsOnceWithoutExecutionOrRetry()
    {
        var (_, id) = await Enqueue(); await using var db = Db();
        await db.SavedSearches.ExecuteUpdateAsync(s => s.SetProperty(x => x.Enabled, false));
        using var factory = Factory(); Assert.True(await Process(factory)); Assert.False(await Process(factory));
        var job = await Job(id); Assert.Equal("failed", job.StatusCode); Assert.Equal("InactiveResource", job.ErrorCode); Assert.Equal(1, job.AttemptCount);
        var attempt = await db.SourceCollectionJobAttempts.SingleAsync(); Assert.Equal("InactiveResource", attempt.ErrorCode);
        Assert.Null(attempt.SourceExecutionId); Assert.Empty(await db.SourceExecutions.ToListAsync()); Assert.Equal(0, feed.Calls);
    }

    [Theory]
    [InlineData(404)]
    [InlineData(501)]
    public async Task NonTransientHttpStatusIsTerminal(int status)
    {
        var (_, id) = await Enqueue(); feed.Error = new("UpstreamHttpError", 502, status);
        using var factory = Factory(); Assert.True(await Process(factory)); Assert.False(await Process(factory));
        Assert.Equal("failed", (await Job(id)).StatusCode); Assert.Equal(1, feed.Calls);
    }

    [Theory]
    [InlineData("succeeded", null, null, "succeeded")]
    [InlineData("failed", "UpstreamTimeout", null, "queued")]
    [InlineData("failed", "UpstreamHttpError", 503, "queued")]
    [InlineData("failed", "UpstreamHttpError", 404, "failed")]
    [InlineData("failed", "UpstreamHttpError", null, "failed")]
    [InlineData("failed", "InactiveResource", null, "failed")]
    [InlineData("cancelled", "CollectionCancelled", null, "queued")]
    [InlineData("cancelled", "RequestCancelled", null, "queued")]
    [InlineData("failed", null, null, "failed")]
    [InlineData("running", null, null, "failed")]
    [InlineData("partial", null, null, "failed")]
    [InlineData("failed", "SQL password http://secret", null, "failed")]
    public async Task RecoveryUsesDurableOutcomeWithoutCallingTransport(string status, string? code, int? http, string expected)
    {
        var (setup, id) = await Enqueue(); using var factory = Factory(); var claimed = await Claim(factory);
        var execution = await Attach(setup, claimed, status, code, http); await Expire(claimed);
        Assert.Equal(1, await Recover(factory)); Assert.Equal(0, await Recover(factory));
        var job = await Job(id); Assert.Equal(expected, job.StatusCode); Assert.Equal(1, job.AttemptCount);
        Assert.Null(job.LeaseToken); Assert.Null(job.LeaseExpiresAt); Assert.Equal(0, feed.Calls);
        await using var db = Db(); var attempt = await db.SourceCollectionJobAttempts.SingleAsync(); Assert.Equal(execution, attempt.SourceExecutionId);
        Assert.Single(await db.SourceExecutions.ToListAsync());
        if (expected == "queued") { Assert.Null(job.SourceExecutionId); Assert.True(job.AvailableAt > DateTimeOffset.UtcNow); Assert.False(await Process(factory)); }
        else Assert.Equal(execution, job.SourceExecutionId);
        if (status is "running" or "partial") Assert.Equal("CollectionRecoveryAmbiguous", job.ErrorCode);
        if (code?.Contains("password") == true) { Assert.Equal("CollectionWorkerError", job.ErrorCode); Assert.Equal("CollectionWorkerError", attempt.ErrorCode); }
    }

    [Fact]
    public async Task MissingExecutionRecoversOnceAndOldTokenCannotFinishOrAttachHistory()
    {
        var (setup, id) = await Enqueue(); using var factory = Factory(); var claimed = await Claim(factory);
        Assert.Equal(0, await Recover(factory)); await Expire(claimed);
        var results = await Task.WhenAll(Recover(factory), Recover(factory)); Assert.Equal(1, results.Sum());
        var queued = await Job(id); Assert.Equal("queued", queued.StatusCode); Assert.Equal(1, queued.AttemptCount);
        await using var scope = factory.Services.CreateAsyncScope(); var queue = scope.ServiceProvider.GetRequiredService<ISourceCollectionJobQueue>();
        Assert.False(await queue.CompleteAsync(id, setup.Workspace, claimed.LeaseToken!.Value, "UpstreamTimeout", default));
        await Available(id); var next = await Claim(factory); Assert.Equal(2, next.AttemptCount);
        Assert.False(await queue.CompleteAsync(id, setup.Workspace, claimed.LeaseToken.Value, "UpstreamTimeout", default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Attach(setup, claimed, "succeeded"));
        await using var db = Db(); Assert.Empty(await db.SourceExecutions.ToListAsync());
        Assert.Equal("CollectionAbandoned", (await db.SourceCollectionJobAttempts.SingleAsync(x => x.AttemptNumber == 1)).ErrorCode);
    }

    [Fact]
    public async Task LiveWorkerSessionPreventsRecoveryEvenWhenLeaseExpires()
    {
        var (_, id) = await Enqueue(); feed.Failures = 0;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        feed.Gate = async token => { entered.TrySetResult(); await release.Task.WaitAsync(token); };
        using var factory = Factory(); var pending = Process(factory);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(20)); var running = await Job(id);
        try { await Expire(running); Assert.Equal(0, await Recover(factory)); Assert.False(await Process(factory)); Assert.Equal(1, feed.Calls); }
        finally { release.TrySetResult(); }
        Assert.True(await pending); Assert.Equal("succeeded", (await Job(id)).StatusCode);
    }

    [Fact]
    public async Task RetryClaimWaitsUntilPreviousSessionGuardIsReleasedWithoutConsumingAttempt()
    {
        var (_, id) = await Enqueue(); using var factory = Factory(); var claimed = await Claim(factory);
        await using var scope = factory.Services.CreateAsyncScope();
        var guard = await scope.ServiceProvider.GetRequiredService<SourceCollectionJobGuard>().TryAcquireAsync(claimed, default);
        Assert.NotNull(guard);
        try
        {
            var queue = scope.ServiceProvider.GetRequiredService<ISourceCollectionJobQueue>();
            Assert.True(await queue.CompleteAsync(id, claimed.WorkspaceId, claimed.LeaseToken!.Value, "UpstreamTimeout", default));
            await Available(id); Assert.Null(await queue.ClaimAsync(default)); Assert.Equal(1, (await Job(id)).AttemptCount);
        }
        finally { await guard.DisposeAsync(); }
        Assert.Equal(2, (await Claim(factory)).AttemptCount);
    }

    [Fact]
    public async Task FencingStopsBusinessWritesIfOldProcessContinuesAfterRecovery()
    {
        var (setup, id) = await Enqueue(); feed.Failures = 0;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        feed.Gate = async token => { entered.TrySetResult(); await release.Task.WaitAsync(token); };
        using var factory = Factory(); var claimed = await Claim(factory);
        await using var old = factory.Services.CreateAsyncScope();
        // No live session guard: simulate loss of the worker's PostgreSQL session during a fetch.
        var pending = old.ServiceProvider.GetRequiredService<ISourceCollectionOrchestrator>().CollectAsync(setup.Workspace,
            setup.Search, setup.Stage, new(claimed.Id, claimed.LeaseToken!.Value, Guid.NewGuid(), "manual"), default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        try { await Expire(claimed); Assert.Equal(1, await Recover(factory)); }
        finally { release.TrySetResult(); }
        Assert.NotNull((await pending).Error);
        await using var db = Db(); Assert.Empty(await db.Opportunities.ToListAsync()); Assert.Empty(await db.SourceExecutions.ToListAsync());
        Assert.Equal("queued", (await Job(id)).StatusCode);
        feed.Gate = null; await Available(id); Assert.True(await Process(factory));
        Assert.Single(await db.Opportunities.ToListAsync()); Assert.Single(await db.SourceExecutions.ToListAsync());
    }

    [Fact]
    public async Task CancellingBackoffCannotBeUndoneByOldFinalization()
    {
        var (_, id) = await Enqueue(); using var factory = Factory(); var claimed = await Claim(factory);
        await using var scope = factory.Services.CreateAsyncScope(); var queue = scope.ServiceProvider.GetRequiredService<ISourceCollectionJobQueue>();
        var finish = queue.CompleteAsync(id, claimed.WorkspaceId, claimed.LeaseToken!.Value, "UpstreamTimeout", default);
        using var client = factory.CreateClient(new() { BaseAddress = new("https://localhost") });
        using var response = await client.PostAsync($"/api/source-collection-jobs/{id}/cancel", null); Assert.True(await finish);
        Assert.Contains(response.StatusCode, new[] { HttpStatusCode.NoContent, HttpStatusCode.Conflict });
        using var repeat = await client.PostAsync($"/api/source-collection-jobs/{id}/cancel", null); Assert.Equal(HttpStatusCode.NoContent, repeat.StatusCode);
        Assert.False(await queue.CompleteAsync(id, claimed.WorkspaceId, claimed.LeaseToken.Value, "UpstreamTimeout", default));
        Assert.False(await Process(factory)); Assert.Equal("cancelled", (await Job(id)).StatusCode); Assert.Equal(0, feed.Calls);
    }
}
