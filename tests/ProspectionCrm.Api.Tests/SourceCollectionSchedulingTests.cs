using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using ProspectionCrm.Api.Dtos.SavedSearches;
using ProspectionCrm.Api.Entities;
using ProspectionCrm.Api.Services;
using ProspectionCrm.Api.Services.Collection;
using Xunit;

namespace ProspectionCrm.Api.Tests;

public sealed class SourceCollectionSchedulingTests : PersistentSourceIdentityFixture
{
    private sealed class Feed : IRssFeedTransport
    {
        public int Calls;
        public bool Fail;
        public Task<RssFeedResponse> FetchAsync(Uri uri, CancellationToken token)
        {
            Interlocked.Increment(ref Calls);
            if (Fail) throw new SourceCollectionException(new("UpstreamTimeout", 504));
            return Task.FromResult(new RssFeedResponse(Encoding.UTF8.GetBytes(
                RssAtomFeedParserTests.Rss("<item><title>Developer</title><guid>scheduled-one</guid></item>")), uri));
        }
    }
    private readonly Feed feed = new();
    private WebApplicationFactory<Program> Factory(bool scheduler = false) => new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        b.UseEnvironment("Testing").UseSetting("ConnectionStrings:DefaultConnection", Postgres.GetConnectionString())
            .UseSetting("SourceCollectionWorker:Enabled", "false").UseSetting("SourceCollectionScheduler:Enabled", scheduler.ToString())
            .UseSetting("SourceCollectionScheduler:PollIntervalSeconds", "1")
            .ConfigureServices(s => { s.RemoveAll<IRssFeedTransport>(); s.AddSingleton<IRssFeedTransport>(feed); }));
    private static HttpClient Client(WebApplicationFactory<Program> factory) => factory.CreateClient(new() { BaseAddress = new("https://localhost") });
    private static string Route(Setup setup) => $"/api/saved-searches/{setup.Search}/schedule";
    private async Task Enable(Setup setup, string time = "09:30")
    {
        await using var db = Db();
        var result = await new SourceCollectionScheduleService(db, new FixedWorkspace(setup.Workspace)).UpdateAsync(setup.Search,
            new() { Enabled = true, DailyUtcTime = time, PipelineStageId = setup.Stage }, default);
        Assert.True(result.Found); Assert.Null(result.Error);
    }
    private async Task Due(Setup setup)
    {
        await using var db = Db();
        await db.SourceCollectionSchedules.Where(x => x.SavedSearchId == setup.Search)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.NextCollectionAt, DateTimeOffset.UtcNow.AddDays(-10)));
    }
    private async Task<int> Run(int batch = 50)
    {
        await using var db = Db();
        return await new SourceCollectionSchedulingService(db, Options.Create(new SourceCollectionSchedulerOptions { BatchSize = batch }),
            NullLogger<SourceCollectionSchedulingService>.Instance).ScheduleDueAsync(default);
    }
    private async Task<SourceCollectionSchedule> Schedule(Setup setup)
    { await using var db = Db(); return await db.SourceCollectionSchedules.AsNoTracking().SingleAsync(x => x.SavedSearchId == setup.Search); }

    [Fact]
    public async Task ConfigurationReadChangeDisableArchiveAndRestoreAreExplicit()
    {
        var setup = await PrepareAsync(); using var factory = Factory(); using var client = Client(factory);
        var initial = await client.GetFromJsonAsync<SavedSearchDto>($"/api/saved-searches/{setup.Search}");
        Assert.False(initial!.Schedule.Enabled); Assert.Null(initial.Schedule.NextCollectionAt);
        using var enabled = await client.PutAsJsonAsync(Route(setup), new { enabled = true, dailyUtcTime = "09:30", pipelineStageId = setup.Stage });
        Assert.Equal(HttpStatusCode.NoContent, enabled.StatusCode);
        var configured = await client.GetFromJsonAsync<SavedSearchDto>($"/api/saved-searches/{setup.Search}");
        Assert.True(configured!.Schedule.Enabled); Assert.Equal("09:30", configured.Schedule.DailyUtcTime);
        Assert.Equal(setup.Stage, configured.Schedule.PipelineStageId); Assert.True(configured.Schedule.NextCollectionAt > DateTimeOffset.UtcNow);
        var list = await client.GetFromJsonAsync<SavedSearchDto[]>("/api/saved-searches");
        Assert.Equal(configured.Schedule, Assert.Single(list!).Schedule);
        await Due(setup); var overdue = (await Schedule(setup)).NextCollectionAt;
        await Enable(setup); Assert.Equal(overdue, (await Schedule(setup)).NextCollectionAt);
        await Enable(setup, "23:59"); Assert.True((await Schedule(setup)).NextCollectionAt > DateTimeOffset.UtcNow);
        Assert.Equal(1439, (await Schedule(setup)).DailyUtcMinute);
        await Due(setup); Assert.Equal(1, await Run());
        using var disabled = await client.PutAsJsonAsync(Route(setup), new { enabled = false });
        Assert.Equal(HttpStatusCode.NoContent, disabled.StatusCode); Assert.Null((await Schedule(setup)).NextCollectionAt);
        Assert.Equal(0, await Run());
        await Enable(setup);
        using var archived = await client.PostAsync($"/api/saved-searches/{setup.Search}/archive", null);
        Assert.Equal(HttpStatusCode.NoContent, archived.StatusCode); Assert.False((await Schedule(setup)).Enabled);
        using var restored = await client.PostAsync($"/api/saved-searches/{setup.Search}/restore", null);
        Assert.Equal(HttpStatusCode.NoContent, restored.StatusCode); Assert.False((await Schedule(setup)).Enabled);
        Assert.Null((await Schedule(setup)).NextCollectionAt);
        await using var db = Db(); Assert.Equal("queued", (await db.SourceCollectionJobs.SingleAsync()).StatusCode);
        Assert.Equal(0, feed.Calls);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"enabled\":true}")]
    [InlineData("{\"enabled\":true,\"dailyUtcTime\":\"24:00\"}")]
    [InlineData("{\"enabled\":true,\"dailyUtcTime\":\"09:30:00\"}")]
    [InlineData("{\"enabled\":false,\"nextCollectionAt\":\"2026-01-01T00:00:00Z\"}")]
    [InlineData("{\"enabled\":false,\"dailyUtcTime\":\"09:30\"}")]
    public async Task StrictConfigurationRejectsInvalidOrUnknownInputs(string json)
    {
        var setup = await PrepareAsync(); using var factory = Factory(); using var client = Client(factory);
        using var response = await client.PutAsync(Route(setup), new StringContent(json, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await using var db = Db(); Assert.Empty(await db.SourceCollectionSchedules.ToListAsync()); Assert.Equal(0, feed.Calls);
    }

    [Fact]
    public async Task ForeignWorkspaceAndForeignPipelineTargetsAreRejected()
    {
        var setup = await PrepareAsync(); await using var db = Db(); var other = await SeedAsync(db, archived: true);
        var pipeline = new Pipeline { WorkspaceId = setup.Workspace, Name = "Other", TypeCode = "custom" };
        var stage = new PipelineStage { Pipeline = pipeline, Name = "Other", CategoryCode = "active" };
        db.Add(stage); await db.SaveChangesAsync();
        using var factory = Factory(); using var client = Client(factory);
        foreach (var target in new[] { other.Stage, stage.Id, Guid.Empty, Guid.NewGuid() })
        {
            using var response = await client.PutAsJsonAsync(Route(setup), new { enabled = true, dailyUtcTime = "09:30", pipelineStageId = target });
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        using var foreign = await client.PutAsJsonAsync(Route(other), new { enabled = false });
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode); Assert.Empty(await db.SourceCollectionSchedules.ToListAsync());
    }

    [Fact]
    public async Task FutureAndDisabledSchedulesCreateNothing()
    {
        var setup = await PrepareAsync(); await Enable(setup); Assert.Equal(0, await Run());
        await using var db = Db();
        await db.SourceCollectionSchedules.ExecuteUpdateAsync(s => s.SetProperty(x => x.Enabled, false).SetProperty(x => x.NextCollectionAt, (DateTimeOffset?)null));
        Assert.Equal(0, await Run()); Assert.Empty(await db.SourceCollectionJobs.ToListAsync());
    }

    [Theory]
    [InlineData("stage")] [InlineData("pipeline")] [InlineData("search")] [InlineData("disabled-search")]
    [InlineData("workspace")] [InlineData("source")] [InlineData("disabled-source")]
    public async Task InvalidTargetIsSuspendedWithoutCrashingAndActivationIsRejected(string kind)
    {
        var setup = await PrepareAsync(); await Enable(setup); await Due(setup); await using var db = Db();
        var now = DateTimeOffset.UtcNow;
        switch (kind)
        {
            case "stage": await db.PipelineStages.ExecuteUpdateAsync(s => s.SetProperty(x => x.ArchivedAt, now)); break;
            case "pipeline": await db.Pipelines.ExecuteUpdateAsync(s => s.SetProperty(x => x.ArchivedAt, now)); break;
            case "search": await db.SavedSearches.ExecuteUpdateAsync(s => s.SetProperty(x => x.ArchivedAt, now)); break;
            case "disabled-search": await db.SavedSearches.ExecuteUpdateAsync(s => s.SetProperty(x => x.Enabled, false)); break;
            case "workspace": await db.Workspaces.ExecuteUpdateAsync(s => s.SetProperty(x => x.ArchivedAt, now)); break;
            case "source": await db.SourceConfigurations.ExecuteUpdateAsync(s => s.SetProperty(x => x.ArchivedAt, now)); break;
            case "disabled-source": await db.SourceConfigurations.ExecuteUpdateAsync(s => s.SetProperty(x => x.Enabled, false)); break;
        }
        Assert.Equal(1, await Run()); Assert.False((await Schedule(setup)).Enabled); Assert.Null((await Schedule(setup)).NextCollectionAt);
        Assert.Empty(await db.SourceCollectionJobs.ToListAsync()); Assert.Equal(0, await Run());
        var result = await new SourceCollectionScheduleService(db, new FixedWorkspace(setup.Workspace)).UpdateAsync(setup.Search,
            new() { Enabled = true, DailyUtcTime = "09:30", PipelineStageId = setup.Stage }, default);
        Assert.NotNull(result.Error);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ActiveManualJobCoalescesAndAdvances(bool running)
    {
        var setup = await PrepareAsync(); await Enable(setup); await Due(setup); await using var db = Db();
        var now = DateTimeOffset.UtcNow;
        var job = new SourceCollectionJob { WorkspaceId = setup.Workspace, PipelineId = setup.Pipeline, PipelineStageId = setup.Stage,
            SavedSearchId = setup.Search, TriggerTypeCode = "manual", StatusCode = running ? "running" : "queued", EnqueuedAt = now, AvailableAt = now,
            StartedAt = running ? now : null, LeaseToken = running ? Guid.NewGuid() : null, LeaseExpiresAt = running ? now.AddMinutes(5) : null };
        db.Add(job); await db.SaveChangesAsync();
        Assert.Equal(1, await Run()); Assert.True((await Schedule(setup)).NextCollectionAt > DateTimeOffset.UtcNow);
        Assert.Equal(job.Id, (await db.SourceCollectionJobs.SingleAsync()).Id); Assert.Equal("manual", job.TriggerTypeCode);
        Assert.Equal(0, await Run());
    }

    [Fact]
    public async Task ConcurrentInstancesAndRestartCatchUpOnlyOnce()
    {
        var setup = await PrepareAsync(); await Enable(setup); await Due(setup);
        var results = await Task.WhenAll(Run(), Run()); Assert.Equal(1, results.Sum());
        using var restarted = Factory(); await using var scope = restarted.Services.CreateAsyncScope();
        Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<ISourceCollectionSchedulingService>().ScheduleDueAsync(default));
        await using var db = Db(); var job = await db.SourceCollectionJobs.SingleAsync();
        Assert.Equal("scheduled", job.TriggerTypeCode); Assert.Equal("queued", job.StatusCode); Assert.Equal(0, job.AttemptCount);
        Assert.True((await Schedule(setup)).NextCollectionAt > DateTimeOffset.UtcNow); Assert.Equal(0, feed.Calls);
    }

    [Fact]
    public async Task LockedSearchIsSkippedAndCommittedDisablePreventsScheduling()
    {
        var setup = await PrepareAsync(); await Enable(setup); await Due(setup);
        await using var db = Db(); await using var tx = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($"""SELECT 1 FROM "SavedSearches" WHERE "Id" = {setup.Search} FOR NO KEY UPDATE""");
        Assert.Equal(0, await Run().WaitAsync(TimeSpan.FromSeconds(5)));
        await db.SourceCollectionSchedules.ExecuteUpdateAsync(s => s.SetProperty(x => x.Enabled, false).SetProperty(x => x.NextCollectionAt, (DateTimeOffset?)null));
        await tx.CommitAsync(); Assert.Equal(0, await Run()); Assert.Empty(await db.SourceCollectionJobs.ToListAsync());
    }

    [Fact]
    public async Task ConcurrentUncommittedManualInsertCoalescesWithoutDeadlock()
    {
        var setup = await PrepareAsync(); await Enable(setup); await Due(setup);
        await using var db = Db(); await using var tx = await db.Database.BeginTransactionAsync();
        var now = DateTimeOffset.UtcNow;
        var job = new SourceCollectionJob { WorkspaceId = setup.Workspace, SavedSearchId = setup.Search, PipelineId = setup.Pipeline,
            PipelineStageId = setup.Stage, StatusCode = "queued", TriggerTypeCode = "manual", EnqueuedAt = now, AvailableAt = now };
        db.Add(job); await db.SaveChangesAsync();
        var scheduling = Run();
        // Wait for the scheduler INSERT to arbitrate the unique index against this uncommitted command.
        await using var observer = new NpgsqlConnection(Postgres.GetConnectionString()); await observer.OpenAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var command = new NpgsqlCommand("""
            SELECT EXISTS (SELECT 1 FROM pg_stat_activity WHERE datname = current_database()
                AND wait_event_type = 'Lock' AND query LIKE '%ON CONFLICT%')
            """, observer);
        while (!(bool)(await command.ExecuteScalarAsync(timeout.Token))!) await Task.Delay(20, timeout.Token);
        await tx.CommitAsync(); Assert.Equal(1, await scheduling.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(job.Id, (await db.SourceCollectionJobs.SingleAsync()).Id);
        Assert.True((await Schedule(setup)).NextCollectionAt > DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task OrdinaryUpdateKeepsScheduleWhileDisableAndPipelineChangeResetIt()
    {
        var setup = await PrepareAsync(); await Enable(setup); await Due(setup); var due = (await Schedule(setup)).NextCollectionAt;
        await using var db = Db();
        var service = new SavedSearchService(db, new FixedWorkspace(setup.Workspace));
        var request = new UpdateSavedSearchRequest { PipelineId = setup.Pipeline, SourceConfigurationId = setup.Configuration, Name = "Renamed" };
        Assert.Null((await service.UpdateAsync(setup.Search, request, default)).Error);
        Assert.Equal(due, (await Schedule(setup)).NextCollectionAt);
        request.Enabled = false; Assert.Null((await service.UpdateAsync(setup.Search, request, default)).Error);
        Assert.False((await Schedule(setup)).Enabled); Assert.Null((await Schedule(setup)).NextCollectionAt);
        request.Enabled = true; Assert.Null((await service.UpdateAsync(setup.Search, request, default)).Error);
        Assert.False((await Schedule(setup)).Enabled);
        await Enable(setup);
        var pipeline = new Pipeline { WorkspaceId = setup.Workspace, Name = "New", TypeCode = "custom" };
        db.Add(pipeline); await db.SaveChangesAsync(); request.PipelineId = pipeline.Id;
        Assert.Null((await service.UpdateAsync(setup.Search, request, default)).Error);
        Assert.Empty(await db.SourceCollectionSchedules.ToListAsync());
    }

    [Fact]
    public async Task HistoricalJobPreventsPipelineChangeAndRollsBackScheduleDeletion()
    {
        var setup = await PrepareAsync(); await Enable(setup); await Due(setup); Assert.Equal(1, await Run());
        await using var db = Db();
        await db.SourceCollectionJobs.ExecuteUpdateAsync(s => s.SetProperty(x => x.StatusCode, "cancelled").SetProperty(x => x.FinishedAt, DateTimeOffset.UtcNow));
        var pipeline = new Pipeline { WorkspaceId = setup.Workspace, Name = "New", TypeCode = "custom" };
        db.Add(pipeline); await db.SaveChangesAsync();
        var result = await new SavedSearchService(db, new FixedWorkspace(setup.Workspace)).UpdateAsync(setup.Search,
            new() { PipelineId = pipeline.Id, SourceConfigurationId = setup.Configuration, Name = "Test" }, default);
        Assert.NotNull(result.Error); Assert.True((await Schedule(setup)).Enabled);
        Assert.Equal(setup.Pipeline, (await Schedule(setup)).PipelineId);
        Assert.Equal("cancelled", (await db.SourceCollectionJobs.SingleAsync()).StatusCode);
    }

    [Fact]
    public async Task HostedSchedulerCatchesUpAfterApplicationRestartWithoutWorker()
    {
        var setup = await PrepareAsync();
        using (var stopped = Factory()) { using var client = Client(stopped); await Enable(setup); await Due(setup); }
        using var restarted = Factory(scheduler: true); using var runningClient = Client(restarted);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var db = Db();
        while (!await db.SourceCollectionJobs.AnyAsync(timeout.Token)) await Task.Delay(25, timeout.Token);
        await restarted.DisposeAsync();
        Assert.Single(await db.SourceCollectionJobs.ToListAsync()); Assert.True((await Schedule(setup)).NextCollectionAt > DateTimeOffset.UtcNow);
        Assert.Equal(0, feed.Calls);
    }

    [Fact]
    public async Task BatchHonorsLimitAndKeepsWorkspaceTargetsSeparate()
    {
        var first = await PrepareAsync(); await using var db = Db(); var second = await SeedAsync(db);
        await Enable(first); await Enable(second); await Due(first); await Due(second);
        Assert.Equal(1, await Run(batch: 1)); Assert.Equal(1, await db.SourceCollectionJobs.CountAsync());
        Assert.Equal(1, await Run()); Assert.Equal(2, await db.SourceCollectionJobs.CountAsync());
        foreach (var setup in new[] { first, second })
        {
            var job = await db.SourceCollectionJobs.SingleAsync(x => x.WorkspaceId == setup.Workspace);
            Assert.Equal(setup.Search, job.SavedSearchId); Assert.Equal(setup.Pipeline, job.PipelineId); Assert.Equal(setup.Stage, job.PipelineStageId);
        }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ScheduledJobFlowsThroughWorkerAndRetryPreservingOrigin(bool retry)
    {
        var setup = await PrepareAsync(); await using var db = Db();
        await db.Opportunities.ExecuteDeleteAsync(); await db.SourceExecutions.ExecuteDeleteAsync();
        await db.SourceConfigurations.ExecuteUpdateAsync(s => s.SetProperty(x => x.SourceTypeCode, "rss"));
        await db.SavedSearches.ExecuteUpdateAsync(s => s.SetProperty(x => x.SearchUrl, "https://feeds.example.org/jobs.xml"));
        await Enable(setup); await Due(setup); Assert.Equal(1, await Run());
        var next = (await Schedule(setup)).NextCollectionAt; Assert.True(next > DateTimeOffset.UtcNow);
        using var factory = Factory(); feed.Fail = retry;
        await using (var scope = factory.Services.CreateAsyncScope())
            Assert.True(await scope.ServiceProvider.GetRequiredService<ISourceCollectionJobProcessor>().ProcessNextAsync(default));
        if (retry)
        {
            var queued = await db.SourceCollectionJobs.AsNoTracking().SingleAsync();
            Assert.Equal("queued", queued.StatusCode); Assert.Equal(1, queued.AttemptCount); Assert.True(queued.AvailableAt > DateTimeOffset.UtcNow);
            Assert.Equal(0, await Run()); Assert.Equal(next, (await Schedule(setup)).NextCollectionAt);
            await db.Database.ExecuteSqlRawAsync("""UPDATE "SourceCollectionJobs" SET "AvailableAt" = statement_timestamp()""");
            feed.Fail = false; await using var scope = factory.Services.CreateAsyncScope();
            Assert.True(await scope.ServiceProvider.GetRequiredService<ISourceCollectionJobProcessor>().ProcessNextAsync(default));
        }
        var job = await db.SourceCollectionJobs.AsNoTracking().SingleAsync(); Assert.Equal("succeeded", job.StatusCode);
        Assert.Equal("scheduled", job.TriggerTypeCode); Assert.Equal(setup.Pipeline, job.PipelineId); Assert.Equal(setup.Stage, job.PipelineStageId);
        Assert.Equal(retry ? 2 : 1, job.AttemptCount); Assert.Equal(job.AttemptCount, await db.SourceCollectionJobAttempts.CountAsync());
        Assert.All(await db.SourceExecutions.ToListAsync(), x => Assert.Equal("scheduled", x.TriggerTypeCode));
        Assert.Equal(job.SourceExecutionId, (await db.SourceCollectionJobAttempts.SingleAsync(x => x.AttemptNumber == job.AttemptCount)).SourceExecutionId);
        Assert.Equal(setup.Stage, (await db.Opportunities.SingleAsync()).PipelineStageId);
        Assert.Equal(next, (await Schedule(setup)).NextCollectionAt); Assert.Equal(0, await Run());
    }

    [Fact]
    public async Task RowFailureRollsBackJobAndContinuesOtherSearches()
    {
        var first = await PrepareAsync(); await using var db = Db(); var second = await SeedAsync(db);
        await Enable(first); await Enable(second); await Due(first); await Due(second);
        // An actual PostgreSQL error AFTER INSERT must roll back the command as well as the schedule.
        // The only interpolated value is a test-generated Guid, formatted as a UUID literal.
#pragma warning disable EF1002
        await db.Database.ExecuteSqlRawAsync($$"""
            CREATE FUNCTION reject_schedule() RETURNS trigger LANGUAGE plpgsql AS $body$
            BEGIN IF NEW."SavedSearchId" = '{{first.Search}}'::uuid THEN RAISE EXCEPTION 'test failure'; END IF; RETURN NEW; END $body$;
            CREATE TRIGGER reject_schedule BEFORE UPDATE ON "SourceCollectionSchedules" FOR EACH ROW EXECUTE FUNCTION reject_schedule();
            """);
#pragma warning restore EF1002
        Assert.Equal(1, await Run()); Assert.True((await Schedule(first)).NextCollectionAt < DateTimeOffset.UtcNow);
        Assert.Equal(second.Search, (await db.SourceCollectionJobs.SingleAsync()).SavedSearchId);
        await db.Database.ExecuteSqlRawAsync("""DROP TRIGGER reject_schedule ON "SourceCollectionSchedules"; DROP FUNCTION reject_schedule();""");
        Assert.Equal(1, await Run()); Assert.Equal(2, await db.SourceCollectionJobs.CountAsync());
    }
}
