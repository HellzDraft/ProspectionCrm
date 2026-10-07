using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ProspectionCrm.Api.Data;
using ProspectionCrm.Api.Dtos.CollectionJobs;
using ProspectionCrm.Api.Dtos.SavedSearches;
using ProspectionCrm.Api.Entities;
using ProspectionCrm.Api.Services.Collection;
using Xunit;

namespace ProspectionCrm.Api.Tests;

public sealed class SourceCollectionJobTests : PersistentSourceIdentityFixture
{
    private const string FeedUrl = "https://feeds.example.org/jobs.xml";
    private const string Jobs = "/api/source-collection-jobs";
    private static string Enqueue(Guid search) => $"/api/saved-searches/{search}/collection-jobs";
    private sealed class NoNetwork : IRssFeedTransport
    {
        public int Calls;
        public Task<RssFeedResponse> FetchAsync(Uri uri, CancellationToken cancellationToken)
        { Interlocked.Increment(ref Calls); throw new InvalidOperationException("Network forbidden in queue tests"); }
    }
    private readonly NoNetwork transport = new();
    private WebApplicationFactory<Program> Factory(params IInterceptor[] interceptors) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseEnvironment("Testing")
            .UseSetting("ConnectionStrings:DefaultConnection", Postgres.GetConnectionString())
            .ConfigureServices(services =>
            {
                services.RemoveAll<IRssFeedTransport>(); services.AddSingleton<IRssFeedTransport>(transport);
                if (interceptors.Length > 0) services.AddDbContext<ProspectionCrmDbContext>(o => o.AddInterceptors(interceptors));
            }));
    private static HttpClient Client(WebApplicationFactory<Program> factory) => factory.CreateClient(new() { BaseAddress = new("https://localhost") });
    private async Task<Setup> RssSetup()
    {
        var setup = await PrepareAsync(); await using var db = Db();
        await db.Opportunities.ExecuteDeleteAsync(); await db.SourceExecutions.ExecuteDeleteAsync();
        await db.SourceConfigurations.ExecuteUpdateAsync(s => s.SetProperty(x => x.SourceTypeCode, "rss"));
        await db.SavedSearches.ExecuteUpdateAsync(s => s.SetProperty(x => x.SearchUrl, FeedUrl));
        return setup;
    }
    private static async Task<SourceCollectionJobDto> Post(HttpClient client, Setup setup)
    {
        using var response = await client.PostAsJsonAsync(Enqueue(setup.Search), new { pipelineStageId = setup.Stage });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var job = (await response.Content.ReadFromJsonAsync<SourceCollectionJobDto>())!;
        Assert.EndsWith($"{Jobs}/{job.Id}", response.Headers.Location!.ToString());
        return job;
    }
    private static async Task<JsonElement> Problem(HttpResponseMessage response, string code, HttpStatusCode status)
    {
        Assert.Equal(status, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(code, json.GetProperty("code").GetString());
        Assert.DoesNotContain("SQL", json.GetRawText()); Assert.DoesNotContain("UX_", json.GetRawText());
        Assert.DoesNotContain("stack", json.GetRawText(), StringComparison.OrdinalIgnoreCase);
        return json;
    }
    private async Task NoEffects()
    {
        await using var db = Db(); Assert.Equal(0, transport.Calls);
        Assert.Empty(await db.SourceExecutions.ToListAsync()); Assert.Empty(await db.Opportunities.ToListAsync());
        Assert.Empty(await db.OpportunitySources.ToListAsync());
    }

    [Fact]
    public async Task EnqueueIsDurableReadableAndNeverExecutes()
    {
        var setup = await RssSetup(); using var factory = Factory(); using var client = Client(factory);
        var before = DateTimeOffset.UtcNow; var job = await Post(client, setup);
        Assert.Equal(setup.Workspace, job.WorkspaceId); Assert.Equal(setup.Search, job.SavedSearchId);
        Assert.Equal(setup.Pipeline, job.PipelineId); Assert.Equal(setup.Stage, job.PipelineStageId);
        Assert.Equal("manual", job.TriggerTypeCode); Assert.Equal("queued", job.StatusCode); Assert.Equal(0, job.AttemptCount);
        Assert.InRange(job.EnqueuedAt, before, DateTimeOffset.UtcNow); Assert.Equal(job.EnqueuedAt, job.AvailableAt);
        Assert.Null(job.StartedAt); Assert.Null(job.FinishedAt); Assert.Null(job.SourceExecutionId); Assert.Null(job.ErrorCode);
        // A new application scope reads the persisted command; no worker has consumed it.
        using var secondFactory = Factory(); using var secondClient = Client(secondFactory);
        using var read = await secondClient.GetAsync($"{Jobs}/{job.Id}"); Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        var json = await read.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(new[] { "attemptCount", "availableAt", "enqueuedAt", "errorCode", "finishedAt", "id", "pipelineId", "pipelineStageId",
            "savedSearchId", "sourceExecutionId", "startedAt", "statusCode", "triggerTypeCode", "workspaceId" },
            json.EnumerateObject().Select(x => x.Name).Order().ToArray());
        var stored = json.Deserialize<SourceCollectionJobDto>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(job with { EnqueuedAt = stored.EnqueuedAt, AvailableAt = stored.AvailableAt }, stored);
        Assert.True((job.EnqueuedAt - stored.EnqueuedAt).Duration() < TimeSpan.FromMilliseconds(1));
        await NoEffects();
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("{")]
    [InlineData("{\"pipelineStageId\":\"secret-invalid-guid\"}")]
    [InlineData("{\"pipelineStageId\":\"00000000-0000-0000-0000-000000000000\"}")]
    [InlineData("{\"pipelineStageId\":null}")]
    public async Task InvalidBodyIsSanitized(string body)
    {
        var setup = await RssSetup(); using var factory = Factory(); using var client = Client(factory);
        using var response = await client.PostAsync(Enqueue(setup.Search), new StringContent(body, Encoding.UTF8, "application/json"));
        var json = await Problem(response, "InvalidRequest", HttpStatusCode.BadRequest);
        Assert.DoesNotContain("secret", json.GetRawText()); await NoEffects();
        await using var db = Db(); Assert.Empty(await db.SourceCollectionJobs.ToListAsync());
    }

    [Fact]
    public async Task UnknownPropertiesAndClientSchedulingAreRejected()
    {
        var setup = await RssSetup(); using var factory = Factory(); using var client = Client(factory);
        foreach (var property in new[] { "unknown", "availableAt", "priority", "triggerTypeCode" })
        {
            using var response = await client.PostAsJsonAsync(Enqueue(setup.Search),
                new Dictionary<string, object> { ["pipelineStageId"] = setup.Stage, [property] = "secret" });
            var json = await Problem(response, "InvalidRequest", HttpStatusCode.BadRequest); Assert.DoesNotContain("secret", json.GetRawText());
        }
        await NoEffects(); await using var db = Db(); Assert.Empty(await db.SourceCollectionJobs.ToListAsync());
    }

    [Fact]
    public async Task SharedPrevalidationMatchesCollectWithoutNetwork()
    {
        var setup = await RssSetup(); using var factory = Factory(); using var client = Client(factory); await using var db = Db();
        await Check(Guid.NewGuid(), setup.Stage, "ResourceNotFound", HttpStatusCode.NotFound);
        await Check(setup.Search, Guid.NewGuid(), "ResourceNotFound", HttpStatusCode.NotFound);
        await db.SourceConfigurations.ExecuteUpdateAsync(s => s.SetProperty(x => x.SourceTypeCode, "other")); await Check(setup.Search, setup.Stage, "UnsupportedSourceType");
        await db.SourceConfigurations.ExecuteUpdateAsync(s => s.SetProperty(x => x.SourceTypeCode, "rss").SetProperty(x => x.Enabled, false)); await Check(setup.Search, setup.Stage, "InactiveResource");
        await db.SourceConfigurations.ExecuteUpdateAsync(s => s.SetProperty(x => x.Enabled, true).SetProperty(x => x.ArchivedAt, DateTimeOffset.UtcNow)); await Check(setup.Search, setup.Stage, "InactiveResource");
        await db.SourceConfigurations.ExecuteUpdateAsync(s => s.SetProperty(x => x.ArchivedAt, (DateTimeOffset?)null));
        await db.SavedSearches.ExecuteUpdateAsync(s => s.SetProperty(x => x.Enabled, false)); await Check(setup.Search, setup.Stage, "InactiveResource");
        await db.SavedSearches.ExecuteUpdateAsync(s => s.SetProperty(x => x.Enabled, true).SetProperty(x => x.ArchivedAt, DateTimeOffset.UtcNow)); await Check(setup.Search, setup.Stage, "InactiveResource");
        await db.SavedSearches.ExecuteUpdateAsync(s => s.SetProperty(x => x.ArchivedAt, (DateTimeOffset?)null).SetProperty(x => x.SearchUrl, (string?)null)); await Check(setup.Search, setup.Stage, "MissingFeedUrl");
        await db.SavedSearches.ExecuteUpdateAsync(s => s.SetProperty(x => x.SearchUrl, "http://localhost/feed")); await Check(setup.Search, setup.Stage, "UnsafeFeedUrl");
        await db.SavedSearches.ExecuteUpdateAsync(s => s.SetProperty(x => x.SearchUrl, FeedUrl).SetProperty(x => x.CriteriaJson, "{\"maxItems\":101}")); await Check(setup.Search, setup.Stage, "InvalidRssCriteria");
        await db.SavedSearches.ExecuteUpdateAsync(s => s.SetProperty(x => x.CriteriaJson, "{}"));
        var pipeline = new Pipeline { WorkspaceId = setup.Workspace, Name = "Other", TypeCode = "custom" };
        var stage = new PipelineStage { Pipeline = pipeline, Name = "Other", CategoryCode = "active" };
        db.PipelineStages.Add(stage); await db.SaveChangesAsync(); await Check(setup.Search, stage.Id, "WrongPipeline");
        await db.Pipelines.Where(x => x.Id == setup.Pipeline).ExecuteUpdateAsync(s => s.SetProperty(x => x.ArchivedAt, DateTimeOffset.UtcNow)); await Check(setup.Search, setup.Stage, "InactiveResource");
        await db.Pipelines.Where(x => x.Id == setup.Pipeline).ExecuteUpdateAsync(s => s.SetProperty(x => x.ArchivedAt, (DateTimeOffset?)null));
        await db.PipelineStages.Where(x => x.Id == setup.Stage).ExecuteUpdateAsync(s => s.SetProperty(x => x.ArchivedAt, DateTimeOffset.UtcNow)); await Check(setup.Search, setup.Stage, "InactiveResource");
        await db.PipelineStages.Where(x => x.Id == setup.Stage).ExecuteUpdateAsync(s => s.SetProperty(x => x.ArchivedAt, (DateTimeOffset?)null));
        // An existing source outside the selected workspace is as unavailable as a missing source.
        var other = await SeedAsync(db, archived: true);
        await db.SavedSearches.Where(x => x.Id == setup.Search).ExecuteUpdateAsync(s => s.SetProperty(x => x.SourceConfigurationId, other.Configuration));
        await Check(setup.Search, setup.Stage, "ResourceNotFound", HttpStatusCode.NotFound);
        await db.SourceExecutions.ExecuteDeleteAsync(); await db.Opportunities.ExecuteDeleteAsync();
        await db.Workspaces.ExecuteUpdateAsync(s => s.SetProperty(x => x.ArchivedAt, DateTimeOffset.UtcNow)); await Check(setup.Search, setup.Stage, "WorkspaceUnavailable");
        await db.Workspaces.ExecuteUpdateAsync(s => s.SetProperty(x => x.ArchivedAt, (DateTimeOffset?)null)); await Check(setup.Search, setup.Stage, "WorkspaceUnavailable");
        Assert.Empty(await db.SourceCollectionJobs.ToListAsync()); await NoEffects();

        async Task Check(Guid search, Guid target, string code, HttpStatusCode status = HttpStatusCode.Conflict)
        {
            foreach (var route in new[] { Enqueue(search), $"/api/saved-searches/{search}/collect" })
            {
                using var response = await client.PostAsJsonAsync(route, new { pipelineStageId = target });
                await Problem(response, code, status);
            }
            Assert.Equal(0, transport.Calls);
        }
    }

    private sealed class EnqueueGate : SaveChangesInterceptor
    {
        private int arrivals;
        public TaskCompletionSource Both { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<SourceCollectionJob>().Any(x => x.State == EntityState.Added))
            {
                if (Interlocked.Increment(ref arrivals) == 2) Both.TrySetResult();
                await Both.Task.WaitAsync(cancellationToken);
            }
            return result;
        }
    }

    [Fact]
    public async Task ConcurrentEnqueuesReturnExactlyOneAcceptedAndOneConflict()
    {
        var setup = await RssSetup(); var gate = new EnqueueGate(); using var factory = Factory(gate); using var client = Client(factory);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var requests = Enumerable.Range(0, 2).Select(_ => client.PostAsJsonAsync(Enqueue(setup.Search), new { pipelineStageId = setup.Stage }, timeout.Token)).ToArray();
        await gate.Both.Task.WaitAsync(timeout.Token); var responses = await Task.WhenAll(requests);
        try
        {
            var accepted = Assert.Single(responses, x => x.StatusCode == HttpStatusCode.Accepted);
            var job = (await accepted.Content.ReadFromJsonAsync<SourceCollectionJobDto>())!;
            var conflict = Assert.Single(responses, x => x.StatusCode == HttpStatusCode.Conflict);
            var json = await Problem(conflict, "CollectionJobAlreadyPending", HttpStatusCode.Conflict);
            Assert.Equal(job.Id, json.GetProperty("existingJobId").GetGuid());
            await using var db = Db(); Assert.Equal(job.Id, (await db.SourceCollectionJobs.SingleAsync()).Id); await NoEffects();
        }
        finally { foreach (var response in responses) response.Dispose(); }
    }

    [Fact]
    public async Task ConcurrentReferenceDeletionReturnsControlledConflictWithoutPersistingJob()
    {
        var setup = await RssSetup();
        var gate = new PersistentSourceIdentityGate(db => db.ChangeTracker.Entries<SourceCollectionJob>().Any(x => x.State == EntityState.Added));
        using var factory = Factory(gate); using var client = Client(factory);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var pending = client.PostAsJsonAsync(Enqueue(setup.Search), new { pipelineStageId = setup.Stage }, timeout.Token);
        await gate.Reached.Task.WaitAsync(timeout.Token);
        try { await using var db = Db(); await db.SavedSearches.ExecuteDeleteAsync(timeout.Token); }
        finally { gate.Release.TrySetResult(); }
        using var response = await pending; await Problem(response, "ConcurrentCollectionJobChange", HttpStatusCode.Conflict);
        await using var check = Db(); Assert.Empty(await check.SourceCollectionJobs.ToListAsync()); await NoEffects();
    }

    [Fact]
    public async Task CancellationIsAtomicIdempotentAndKeepsRow()
    {
        var setup = await RssSetup(); using var factory = Factory(); using var client = Client(factory); var job = await Post(client, setup);
        var responses = await Task.WhenAll(client.PostAsync($"{Jobs}/{job.Id}/cancel", null), client.PostAsync($"{Jobs}/{job.Id}/cancel", null));
        foreach (var response in responses) { using (response) Assert.Equal(HttpStatusCode.NoContent, response.StatusCode); }
        await using var db = Db(); var row = await db.SourceCollectionJobs.AsNoTracking().SingleAsync();
        Assert.Equal("cancelled", row.StatusCode); Assert.NotNull(row.FinishedAt); Assert.Null(row.StartedAt);
        using var repeat = await client.PostAsync($"{Jobs}/{job.Id}/cancel", null); Assert.Equal(HttpStatusCode.NoContent, repeat.StatusCode);
        Assert.Equal(row.FinishedAt, (await db.SourceCollectionJobs.AsNoTracking().SingleAsync()).FinishedAt); await NoEffects();
    }

    [Theory]
    [InlineData("running")]
    [InlineData("succeeded")]
    [InlineData("failed")]
    [InlineData("cancelled")]
    public async Task TerminalJobsAllowNewEnqueueAndOnlyQueuedOrCancelledCanBeCancelled(string status)
    {
        var setup = await RssSetup(); using var factory = Factory(); using var client = Client(factory); var job = await Post(client, setup);
        await using var db = Db(); var row = await db.SourceCollectionJobs.SingleAsync();
        row.StatusCode = status; row.StartedAt = row.AvailableAt; row.FinishedAt = status == "running" ? null : DateTimeOffset.UtcNow;
        row.ErrorCode = status == "failed" ? "UpstreamTimeout" : null;
        if (status == "succeeded")
        {
            var execution = new SourceExecution { WorkspaceId = setup.Workspace, SavedSearchId = setup.Search,
                SourceConfigurationId = setup.Configuration, TriggerTypeCode = "manual", StatusCode = "succeeded" };
            db.SourceExecutions.Add(execution); row.SourceExecutionId = execution.Id;
        }
        await db.SaveChangesAsync();
        using var cancel = await client.PostAsync($"{Jobs}/{job.Id}/cancel", null);
        if (status == "cancelled") Assert.Equal(HttpStatusCode.NoContent, cancel.StatusCode);
        else await Problem(cancel, "CollectionJobNotCancellable", HttpStatusCode.Conflict);
        if (status == "running")
        {
            using var enqueue = await client.PostAsJsonAsync(Enqueue(setup.Search), new { pipelineStageId = setup.Stage });
            await Problem(enqueue, "CollectionJobAlreadyPending", HttpStatusCode.Conflict);
        }
        else { var next = await Post(client, setup); Assert.NotEqual(job.Id, next.Id); Assert.Equal(2, await db.SourceCollectionJobs.CountAsync()); }
        Assert.Equal(status, (await db.SourceCollectionJobs.AsNoTracking().SingleAsync(x => x.Id == job.Id)).StatusCode); Assert.Equal(0, transport.Calls);
    }

    [Fact]
    public async Task NewPrincipalKeyPreservesUnreferencedSearchEditsAndProtectsJobHistory()
    {
        var setup = await RssSetup(); using var factory = Factory(); using var client = Client(factory); await using var db = Db();
        var pipeline = new Pipeline { WorkspaceId = setup.Workspace, Name = "Other", TypeCode = "custom" };
        var stage = new PipelineStage { Pipeline = pipeline, Name = "Other", CategoryCode = "active" };
        db.PipelineStages.Add(stage); await db.SaveChangesAsync();
        using var move = await client.PutAsJsonAsync($"/api/saved-searches/{setup.Search}", Request(pipeline.Id));
        Assert.Equal(HttpStatusCode.NoContent, move.StatusCode);
        var job = await Post(client, setup with { Pipeline = pipeline.Id, Stage = stage.Id });
        using var edit = await client.PutAsJsonAsync($"/api/saved-searches/{setup.Search}", Request(pipeline.Id));
        Assert.Equal(HttpStatusCode.NoContent, edit.StatusCode);
        using var cancel = await client.PostAsync($"{Jobs}/{job.Id}/cancel", null); Assert.Equal(HttpStatusCode.NoContent, cancel.StatusCode);
        using var blocked = await client.PutAsJsonAsync($"/api/saved-searches/{setup.Search}", Request(setup.Pipeline));
        Assert.Equal(HttpStatusCode.BadRequest, blocked.StatusCode);
        var body = await blocked.Content.ReadAsStringAsync(); Assert.DoesNotContain("SQL", body); Assert.DoesNotContain("FK_", body);
        Assert.Equal(pipeline.Id, (await db.SavedSearches.AsNoTracking().SingleAsync()).PipelineId);
        await NoEffects();
        UpdateSavedSearchRequest Request(Guid id) => new() { Name = "Edited", PipelineId = id,
            SourceConfigurationId = setup.Configuration, SearchUrl = FeedUrl };
    }

    [Fact]
    public async Task PaginationFiltersOrderAndWorkspaceIsolationAreAppliedInSql()
    {
        var setup = await RssSetup(); using var factory = Factory(); using var client = Client(factory); await using var db = Db();
        var now = DateTimeOffset.UtcNow.AddMinutes(-1);
        for (var i = 0; i < 4; i++) db.SourceCollectionJobs.Add(new SourceCollectionJob
        {
            WorkspaceId = setup.Workspace, SavedSearchId = setup.Search, PipelineId = setup.Pipeline, PipelineStageId = setup.Stage,
            TriggerTypeCode = i == 3 ? "scheduled" : "manual", StatusCode = "cancelled", EnqueuedAt = now,
            AvailableAt = now, FinishedAt = now
        });
        var foreign = await SeedAsync(db, archived: true);
        var foreignJob = new SourceCollectionJob { WorkspaceId = foreign.Workspace, SavedSearchId = foreign.Search, PipelineId = foreign.Pipeline,
            PipelineStageId = foreign.Stage, TriggerTypeCode = "event", StatusCode = "queued", EnqueuedAt = now, AvailableAt = now };
        db.SourceCollectionJobs.Add(foreignJob); await db.SaveChangesAsync();
        var expected = await db.SourceCollectionJobs.Where(x => x.WorkspaceId == setup.Workspace).OrderByDescending(x => x.EnqueuedAt)
            .ThenByDescending(x => x.Id).Select(x => x.Id).ToListAsync();
        var page = (await client.GetFromJsonAsync<SourceCollectionJobsPageDto>($"{Jobs}?offset=1&limit=2"))!;
        Assert.Equal(4, page.TotalCount); Assert.True(page.HasMore); Assert.Equal(1, page.Offset); Assert.Equal(2, page.Limit);
        Assert.Equal(expected.Skip(1).Take(2), page.Items.Select(x => x.Id));
        var defaults = (await client.GetFromJsonAsync<SourceCollectionJobsPageDto>(Jobs))!;
        Assert.Equal(0, defaults.Offset); Assert.Equal(50, defaults.Limit); Assert.False(defaults.HasMore);
        Assert.Equal(expected, defaults.Items.Select(x => x.Id));
        page = (await client.GetFromJsonAsync<SourceCollectionJobsPageDto>($"{Jobs}?statusCode=cancelled&triggerTypeCode=manual&savedSearchId={setup.Search}"))!;
        Assert.Equal(3, page.TotalCount); Assert.Equal(3, page.Items.Count);
        foreach (var query in new[] { "offset=100", "statusCode=running", $"savedSearchId={foreign.Search}", "triggerTypeCode=retry" })
        {
            page = (await client.GetFromJsonAsync<SourceCollectionJobsPageDto>($"{Jobs}?{query}"))!;
            Assert.Empty(page.Items); Assert.False(page.HasMore); Assert.Equal(query == "offset=100" ? 4 : 0, page.TotalCount);
        }
        foreach (var id in new[] { foreignJob.Id, Guid.NewGuid() })
        {
            using var read = await client.GetAsync($"{Jobs}/{id}"); await Problem(read, "ResourceNotFound", HttpStatusCode.NotFound);
            using var cancel = await client.PostAsync($"{Jobs}/{id}/cancel", null); await Problem(cancel, "ResourceNotFound", HttpStatusCode.NotFound);
        }
        Assert.Equal("queued", (await db.SourceCollectionJobs.AsNoTracking().SingleAsync(x => x.Id == foreignJob.Id)).StatusCode);
        Assert.Equal(0, transport.Calls);
    }

    [Theory]
    [InlineData("offset=-1", "InvalidPagination")]
    [InlineData("limit=0", "InvalidPagination")]
    [InlineData("limit=201", "InvalidPagination")]
    [InlineData("statusCode=secret", "InvalidStatusCode")]
    [InlineData("triggerTypeCode=secret", "InvalidTriggerTypeCode")]
    [InlineData("offset=secret", "InvalidRequest")]
    [InlineData("savedSearchId=secret", "InvalidRequest")]
    public async Task InvalidQueriesHaveControlledErrors(string query, string code)
    {
        await RssSetup(); using var factory = Factory(); using var client = Client(factory);
        using var response = await client.GetAsync($"{Jobs}?{query}"); var json = await Problem(response, code, HttpStatusCode.BadRequest);
        Assert.DoesNotContain("secret", json.GetRawText()); await NoEffects();
    }
}
