using System.Data.Common;
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
using Npgsql;
using ProspectionCrm.Api.Data;
using ProspectionCrm.Api.Dtos.Collection;
using ProspectionCrm.Api.Dtos.SavedSearches;
using ProspectionCrm.Api.Dtos.Setup;
using ProspectionCrm.Api.Dtos.SourceConfigurations;
using ProspectionCrm.Api.Services.Collection;
using ProspectionCrm.Api.Services;
using Xunit;

namespace ProspectionCrm.Api.Tests;

public sealed class RssCollectionTests : PersistentSourceIdentityFixture
{
    private const string FeedUrl = "https://feeds.example.org/jobs.xml";
    private const string GoodItem = "<item><title>Developer</title><guid>one</guid><link>https://jobs.example.org/one</link><description>Details</description></item>";
    private static string Rss(string items = GoodItem) => RssAtomFeedParserTests.Rss(items);
    private static string Route(Guid id) => $"/api/saved-searches/{id}/collect";
    private WebApplicationFactory<Program> Factory(FakeTransport fake, params IInterceptor[] interceptors) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseEnvironment("Testing")
            .UseSetting("ConnectionStrings:DefaultConnection", Postgres.GetConnectionString())
            .ConfigureServices(services =>
            {
                services.RemoveAll<IRssFeedTransport>(); services.AddSingleton<IRssFeedTransport>(fake);
                if (interceptors.Length > 0) services.AddDbContext<ProspectionCrmDbContext>(o => o.AddInterceptors(interceptors));
            }));
    private static HttpClient Client(WebApplicationFactory<Program> factory) => factory.CreateClient(new() { BaseAddress = new("https://localhost") });
    private async Task<Setup> RssSetup(string criteria = "{}")
    {
        var setup = await PrepareAsync(); await using var db = Db();
        await db.Opportunities.ExecuteDeleteAsync(); await db.SourceExecutions.ExecuteDeleteAsync();
        await db.SourceConfigurations.ExecuteUpdateAsync(s => s.SetProperty(x => x.SourceTypeCode, "rss")
            .SetProperty(x => x.ConfigurationJson, "{\"token\":\"not-for-network-or-history\"}"));
        await db.SavedSearches.ExecuteUpdateAsync(s => s.SetProperty(x => x.SearchUrl, FeedUrl).SetProperty(x => x.CriteriaJson, criteria));
        return setup;
    }
    private static async Task<JsonElement> Json(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).Clone();
    private static async Task<JsonElement> Get(HttpClient client, string route)
    {
        using var response = await client.GetAsync(route); Assert.Equal(HttpStatusCode.OK, response.StatusCode); return await Json(response);
    }
    private static async Task<SourceCollectionDto> Collect(HttpClient client, Guid search, Guid stage)
    {
        using var response = await client.PostAsJsonAsync(Route(search), new { pipelineStageId = stage });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var result = (await response.Content.ReadFromJsonAsync<SourceCollectionDto>())!;
        Assert.EndsWith($"/api/source-executions/{result.Ingestion.Execution.Id}", response.Headers.Location!.ToString());
        return result;
    }

    [Fact]
    public async Task RssSuccessReplayDuplicatesAndDurableCompanyFallbackUseRealIngestion()
    {
        var setup = await RssSetup("{\"defaultCompanyName\":\" Employer \"}");
        var fake = new FakeTransport(Rss(GoodItem + "<item><title> </title><guid>bad</guid></item>" + GoodItem));
        using var factory = Factory(fake); using var client = Client(factory);
        var first = await Collect(client, setup.Search, setup.Stage);
        Assert.Equal(new SourceCollectionSummaryDto("rss", "rss2", 3, 2, 1, false), first.Summary);
        Assert.Equal(2, first.Ingestion.Execution.ItemsFound); Assert.Equal(1, first.Ingestion.Execution.ItemsCreated);
        Assert.Equal(1, first.Ingestion.Execution.ItemsIgnored);
        Assert.Equal(new[] { "created", "ignored" }, first.Ingestion.Items.Select(x => x.Outcome));
        var history = await Get(client, $"/api/source-executions/{first.Ingestion.Execution.Id}/history");
        Assert.True(history.GetProperty("execution").GetProperty("historyAvailable").GetBoolean());
        Assert.Equal(FeedUrl, history.GetProperty("contextSnapshot").GetProperty("savedSearch").GetProperty("searchUrl").GetString());
        Assert.DoesNotContain("not-for-network-or-history", history.GetRawText());
        var items = await Get(client, $"/api/source-executions/{first.Ingestion.Execution.Id}/items");
        Assert.Equal("Employer", items.GetProperty("items")[0].GetProperty("companyName").GetString());
        var replay = await Collect(client, setup.Search, setup.Stage);
        Assert.NotEqual(first.Ingestion.Execution.Id, replay.Ingestion.Execution.Id);
        Assert.Equal(1, replay.Ingestion.Execution.ItemsUpdated); Assert.Equal(1, replay.Ingestion.Execution.ItemsIgnored);
        // New strong identities still find the original title/company through durable observations.
        fake.Xml = Rss(GoodItem.Replace("one", "two", StringComparison.Ordinal));
        var fallback = await Collect(client, setup.Search, setup.Stage);
        Assert.Equal(1, fallback.Ingestion.Execution.ItemsUpdated);
        Assert.Equal(first.Ingestion.Items[0].OpportunityId, fallback.Ingestion.Items[0].OpportunityId);
        await using var db = Db();
        Assert.Equal(1, await db.Opportunities.CountAsync()); Assert.Equal(2, await db.OpportunitySources.CountAsync());
        Assert.Equal(3, await db.SourceExecutions.CountAsync()); Assert.Empty(await db.Companies.ToListAsync());
        Assert.Equal("Details", (await db.Opportunities.SingleAsync()).Notes);
    }

    [Fact]
    public async Task AtomAndMaxItemsPreserveAdapterSummaryAndBusinessCounters()
    {
        var setup = await RssSetup("{\"maxItems\":1}");
        var fake = new FakeTransport(RssAtomFeedParserTests.Atom("""
            <entry><title>First</title><id>A</id><link href='../first'/><content type='html'>&lt;b&gt;Body&lt;/b&gt;</content></entry>
            <entry><title>Second</title><id>B</id><summary>Other</summary></entry><entry><title>Invalid</title></entry>
            """));
        using var factory = Factory(fake); using var client = Client(factory);
        var result = await Collect(client, setup.Search, setup.Stage);
        Assert.Equal(new SourceCollectionSummaryDto("rss", "atom1", 3, 1, 1, true), result.Summary);
        Assert.Equal(1, result.Ingestion.Execution.ItemsFound);
        await using var db = Db();
        Assert.Equal("Body", (await db.Opportunities.SingleAsync()).Notes);
        var source = await db.OpportunitySources.SingleAsync();
        Assert.Equal("A", source.ExternalId); Assert.Equal("https://feeds.example.org/first", source.SourceUrl);
    }

    [Fact]
    public async Task AllPostAttemptFailuresHaveControlledProblemsAndReadableEmptyHistory()
    {
        var setup = await RssSetup(); var fake = new FakeTransport(Rss());
        using var factory = Factory(fake); using var client = Client(factory);
        var errors = new[] { new SourceAdapterError("DnsResolutionFailed", 502), new("UnsafeResolvedAddress", 502),
            new("UpstreamTlsFailure", 502), new("UpstreamTimeout", 504), new("UpstreamHttpError", 502, 503),
            new("UnsafeRedirect", 502), new("TooManyRedirects", 502), new("ResponseTooLarge", 422),
            new("UnsupportedContentType", 422), new("UpstreamTransportError", 502) };
        foreach (var error in errors)
        {
            fake.Failure = new SourceCollectionException(error);
            await Failed(error.Code, error.StatusCode, error.UpstreamStatusCode);
        }
        fake.Failure = new Exception("secret raw upstream response http://private.internal/password");
        await Failed("CollectionInternalError", 500);
        fake.Failure = null; fake.Xml = "<rss>"; await Failed("InvalidFeed", 422);
        fake.Xml = "<html/>"; await Failed("InvalidFeed", 422);
        fake.Xml = Rss("<item><title>Job without identity</title></item>"); await Failed("NoUsableFeedEntries", 422);
        await using var db = Db();
        Assert.Equal(14, await db.SourceExecutions.CountAsync());
        Assert.Empty(await db.Opportunities.ToListAsync()); Assert.Empty(await db.OpportunitySources.ToListAsync());
        Assert.Empty(await db.SourceExecutionItems.ToListAsync());

        async Task Failed(string code, int status, int? upstream = null)
        {
            using var response = await client.PostAsJsonAsync(Route(setup.Search), new { pipelineStageId = setup.Stage });
            Assert.Equal(status, (int)response.StatusCode); var problem = await Json(response);
            Assert.Equal(code, problem.GetProperty("code").GetString());
            Assert.Equal(upstream, problem.GetProperty("upstreamStatusCode").ValueKind == JsonValueKind.Null ? (int?)null : problem.GetProperty("upstreamStatusCode").GetInt32());
            Assert.Equal(JsonValueKind.Null, problem.GetProperty("itemIndex").ValueKind);
            Assert.DoesNotContain("private", problem.GetRawText()); Assert.DoesNotContain(FeedUrl, problem.GetRawText());
            var id = problem.GetProperty("executionId").GetGuid();
            var execution = await Get(client, $"/api/source-executions/{id}");
            Assert.Equal("failed", execution.GetProperty("statusCode").GetString());
            Assert.Equal(code, execution.GetProperty("errorMessage").GetString());
            Assert.Equal(0, execution.GetProperty("itemsFound").GetInt32());
            Assert.True(execution.GetProperty("historyAvailable").GetBoolean());
            Assert.Equal(setup.Configuration, execution.GetProperty("sourceConfigurationId").GetGuid());
            Assert.NotEqual(JsonValueKind.Null, execution.GetProperty("finishedAt").ValueKind);
            var history = await Get(client, $"/api/source-executions/{id}/history");
            Assert.Equal(FeedUrl, history.GetProperty("contextSnapshot").GetProperty("savedSearch").GetProperty("searchUrl").GetString());
            var page = await Get(client, $"/api/source-executions/{id}/items");
            Assert.True(page.GetProperty("historyAvailable").GetBoolean());
            Assert.Empty(page.GetProperty("items").EnumerateArray());
        }
    }

    [Fact]
    public async Task PreflightAndInvalidBodiesNeverAttemptNetworkOrCreateExecution()
    {
        var setup = await RssSetup(); var fake = new FakeTransport(Rss());
        using var factory = Factory(fake); using var client = Client(factory); await using var db = Db();
        foreach (var body in new[] { "{}", "null", "{\"pipelineStageId\":\"bad\"}", "{", $"{{\"pipelineStageId\":\"{setup.Stage}\",\"unknown\":true}}" })
        {
            using var response = await client.PostAsync(Route(setup.Search), new StringContent(body, Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode); Assert.Equal("InvalidRequest", (await Json(response)).GetProperty("code").GetString());
        }
        await Check(Guid.NewGuid(), setup.Stage, "ResourceNotFound", 404);
        await Check(setup.Search, Guid.NewGuid(), "ResourceNotFound", 404);
        await db.SourceConfigurations.ExecuteUpdateAsync(s => s.SetProperty(x => x.SourceTypeCode, "RSS")); await Check(setup.Search, setup.Stage, "UnsupportedSourceType");
        await db.SourceConfigurations.ExecuteUpdateAsync(s => s.SetProperty(x => x.SourceTypeCode, "rss").SetProperty(x => x.Enabled, false)); await Check(setup.Search, setup.Stage, "InactiveResource");
        await db.SourceConfigurations.ExecuteUpdateAsync(s => s.SetProperty(x => x.Enabled, true));
        await db.SavedSearches.ExecuteUpdateAsync(s => s.SetProperty(x => x.SearchUrl, (string?)null)); await Check(setup.Search, setup.Stage, "MissingFeedUrl");
        await db.SavedSearches.ExecuteUpdateAsync(s => s.SetProperty(x => x.SearchUrl, "http://localhost/feed")); await Check(setup.Search, setup.Stage, "UnsafeFeedUrl");
        await db.SavedSearches.ExecuteUpdateAsync(s => s.SetProperty(x => x.SearchUrl, FeedUrl).SetProperty(x => x.CriteriaJson, "{\"maxItems\":101}")); await Check(setup.Search, setup.Stage, "InvalidRssCriteria");
        await db.SavedSearches.ExecuteUpdateAsync(s => s.SetProperty(x => x.CriteriaJson, "{}").SetProperty(x => x.Enabled, false)); await Check(setup.Search, setup.Stage, "InactiveResource");
        await db.SavedSearches.ExecuteUpdateAsync(s => s.SetProperty(x => x.Enabled, true));
        var otherPipeline = new ProspectionCrm.Api.Entities.Pipeline { WorkspaceId = setup.Workspace, Name = "Other", TypeCode = "custom" };
        var otherStage = new ProspectionCrm.Api.Entities.PipelineStage { Pipeline = otherPipeline, Name = "Other", CategoryCode = "active" };
        db.PipelineStages.Add(otherStage); await db.SaveChangesAsync();
        await Check(setup.Search, otherStage.Id, "WrongPipeline");
        await db.PipelineStages.Where(x => x.Id == setup.Stage).ExecuteUpdateAsync(s => s.SetProperty(x => x.ArchivedAt, DateTimeOffset.UtcNow)); await Check(setup.Search, setup.Stage, "InactiveResource");
        await db.Workspaces.ExecuteUpdateAsync(s => s.SetProperty(x => x.ArchivedAt, DateTimeOffset.UtcNow)); await Check(setup.Search, setup.Stage, "WorkspaceUnavailable");
        Assert.Equal(0, fake.Calls); Assert.Empty(await db.SourceExecutions.ToListAsync());
        async Task Check(Guid search, Guid stage, string code, int status = 409)
        {
            using var response = await client.PostAsJsonAsync(Route(search), new { pipelineStageId = stage });
            Assert.Equal(status, (int)response.StatusCode); var problem = await Json(response);
            Assert.Equal(code, problem.GetProperty("code").GetString()); Assert.Equal(JsonValueKind.Null, problem.GetProperty("executionId").ValueKind);
        }
    }

    [Theory]
    [InlineData("url")][InlineData("criteria")][InlineData("type")][InlineData("enabled")][InlineData("archive")]
    public async Task ChangesDuringNetworkRejectOldContextAndPreserveInitialSnapshot(string change)
    {
        var setup = await RssSetup(); var fake = new FakeTransport(Rss()) { Gated = true };
        using var factory = Factory(fake); using var client = Client(factory);
        var pending = client.PostAsJsonAsync(Route(setup.Search), new { pipelineStageId = setup.Stage });
        await fake.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        try
        {
            HttpResponseMessage changed;
            if (change is "url" or "criteria")
                changed = await client.PutAsJsonAsync($"/api/saved-searches/{setup.Search}", new UpdateSavedSearchRequest
                { Name = "Search", PipelineId = setup.Pipeline, SourceConfigurationId = setup.Configuration,
                    SearchUrl = change == "url" ? "https://feeds.example.org/changed" : FeedUrl, CriteriaJson = change == "criteria" ? "{\"maxItems\":1}" : "{}" });
            else if (change == "archive") changed = await client.PostAsync($"/api/source-configurations/{setup.Configuration}/archive", null);
            else changed = await client.PutAsJsonAsync($"/api/source-configurations/{setup.Configuration}", new UpdateSourceConfigurationRequest
            { Name = "Source", SourceTypeCode = change == "type" ? "manual" : "rss", Enabled = change != "enabled" });
            using (changed) Assert.Equal(HttpStatusCode.NoContent, changed.StatusCode);
        }
        finally { fake.Release.TrySetResult(); }
        using var response = await pending;
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode); var problem = await Json(response);
        Assert.Equal("CollectionConfigurationChanged", problem.GetProperty("code").GetString());
        var id = problem.GetProperty("executionId").GetGuid();
        var history = await Get(client, $"/api/source-executions/{id}/history");
        Assert.Equal(FeedUrl, history.GetProperty("contextSnapshot").GetProperty("savedSearch").GetProperty("searchUrl").GetString());
        Assert.Equal("rss", history.GetProperty("contextSnapshot").GetProperty("sourceConfiguration").GetProperty("sourceTypeCode").GetString());
        await using var db = Db();
        Assert.Single(await db.SourceExecutions.ToListAsync()); Assert.Empty(await db.SourceExecutionItems.ToListAsync());
        Assert.Empty(await db.Opportunities.ToListAsync()); Assert.Empty(await db.OpportunitySources.ToListAsync());
    }

    [Fact]
    public async Task FingerprintDetectsContentChangeWithoutUpdatedAtChange()
    {
        var setup = await RssSetup(); var fake = new FakeTransport(Rss()) { Gated = true };
        using var factory = Factory(fake); using var client = Client(factory);
        var pending = client.PostAsJsonAsync(Route(setup.Search), new { pipelineStageId = setup.Stage });
        await fake.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        try
        {
            await using var db = Db();
            await db.SourceConfigurations.ExecuteUpdateAsync(s => s.SetProperty(x => x.ConfigurationJson, "{\"changed\":true}"));
            Assert.Null((await db.SourceConfigurations.SingleAsync()).UpdatedAt);
        }
        finally { fake.Release.TrySetResult(); }
        using var response = await pending;
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("CollectionConfigurationChanged", (await Json(response)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task NoConnectionOrTransactionIsHeldDuringNetworkAndWorkspaceIsolationRemainsIntact()
    {
        var setup = await RssSetup(); await using var db = Db(); var foreign = await SeedAsync(db, archived: true);
        var fake = new FakeTransport(Rss()) { Gated = true }; var observer = new Connections();
        using var factory = Factory(fake, observer); using var client = Client(factory);
        using (var denied = await client.PostAsJsonAsync(Route(foreign.Search), new { pipelineStageId = foreign.Stage }))
        { Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode); Assert.DoesNotContain(FeedUrl, await denied.Content.ReadAsStringAsync()); }
        Assert.Equal(0, fake.Calls);
        var pending = client.PostAsJsonAsync(Route(setup.Search), new { pipelineStageId = setup.Stage });
        await fake.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        try
        {
            Assert.Equal(0, observer.OpenCount);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await db.SourceConfigurations.Where(x => x.Id == foreign.Configuration).ExecuteUpdateAsync(s => s.SetProperty(x => x.Name, "Changed independently"), timeout.Token);
            using var read = await client.GetAsync($"/api/saved-searches/{setup.Search}", timeout.Token);
            Assert.Equal(HttpStatusCode.OK, read.StatusCode); Assert.Equal(0, observer.OpenCount);
            Assert.False(pending.IsCompleted);
        }
        finally { fake.Release.TrySetResult(); }
        using var response = await pending; Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task CancellationDuringRetrievalPersistsUsingIndependentFinalizationToken()
    {
        var setup = await RssSetup(); var fake = new FakeTransport(Rss()) { Gated = true };
        using var factory = Factory(fake); using var client = Client(factory);
        await using var scope = factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<ISourceCollectionService>();
        using var cancellation = new CancellationTokenSource();
        var pending = service.CollectAsync(setup.Search, new() { PipelineStageId = setup.Stage }, cancellation.Token);
        await fake.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20)); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        await using var db = Db(); var execution = await db.SourceExecutions.SingleAsync();
        Assert.Equal("cancelled", execution.StatusCode); Assert.Equal("CollectionCancelled", execution.ErrorMessage);
        Assert.Equal(0, execution.ItemsFound); Assert.NotNull(execution.FinishedAt);
        Assert.Empty(await db.SourceExecutionItems.ToListAsync()); Assert.Empty(await db.Opportunities.ToListAsync()); Assert.Empty(await db.OpportunitySources.ToListAsync());
        var page = await Get(client, $"/api/source-executions/{execution.Id}/items"); Assert.True(page.GetProperty("historyAvailable").GetBoolean());
    }

    [Fact]
    public async Task IngestionConflictPreservesItsCodeIndexAndSingleExecution()
    {
        var setup = await RssSetup(); var fake = new FakeTransport(Rss(GoodItem + GoodItem.Replace("one", "two").Replace("Developer", "Other")));
        using var factory = Factory(fake); using var client = Client(factory);
        await Collect(client, setup.Search, setup.Stage);
        fake.Xml = Rss(GoodItem.Replace("<guid>one</guid>", "<guid>two</guid>"));
        using var response = await client.PostAsJsonAsync(Route(setup.Search), new { pipelineStageId = setup.Stage });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode); var problem = await Json(response);
        Assert.Equal("AmbiguousIdentity", problem.GetProperty("code").GetString()); Assert.Equal(0, problem.GetProperty("itemIndex").GetInt32());
        await using var db = Db(); Assert.Equal(2, await db.SourceExecutions.CountAsync()); Assert.Equal(2, await db.Opportunities.CountAsync());
        var id = problem.GetProperty("executionId").GetGuid();
        Assert.Equal("rejected", (await db.SourceExecutionItems.SingleAsync(x => x.SourceExecutionId == id)).OutcomeCode);
    }

    [Fact]
    public async Task HistoryPersistenceFailureDoesNotReplaceOriginalNetworkError()
    {
        var setup = await RssSetup(); var fake = new FakeTransport(Rss()) { Gated = true, Failure = new SourceCollectionException(new("UpstreamTimeout", 504)) };
        using var factory = Factory(fake); using var client = Client(factory);
        var pending = client.PostAsJsonAsync(Route(setup.Search), new { pipelineStageId = setup.Stage });
        await fake.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        try { await using var db = Db(); await db.SavedSearches.ExecuteDeleteAsync(); }
        finally { fake.Release.TrySetResult(); }
        using var response = await pending; Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
        var problem = await Json(response); Assert.Equal("UpstreamTimeout", problem.GetProperty("code").GetString());
        Assert.Equal(JsonValueKind.Null, problem.GetProperty("executionId").ValueKind); Assert.DoesNotContain("SQL", problem.GetRawText());
    }

    [Fact]
    public async Task Phase63FreshReconstructionThroughHttpKeepsEfModelAndMigrationChainUnchanged()
    {
        await using var db = Db(); Assert.Empty(await db.Database.GetAppliedMigrationsAsync());
        await db.Database.MigrateAsync(); Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.False(db.Database.HasPendingModelChanges()); Assert.Equal("20261006101923_Phase622PersistentSourceIdentities", db.Database.GetMigrations().Last());
        var fake = new FakeTransport(Rss()); using var factory = Factory(fake); using var client = Client(factory);
        using var bootstrap = await client.PostAsync("/api/setup/bootstrap", null); Assert.Equal(HttpStatusCode.Created, bootstrap.StatusCode);
        using var initial = await client.PostAsync("/api/setup/initial-pipelines", null); Assert.Equal(HttpStatusCode.Created, initial.StatusCode);
        var pipelines = (await initial.Content.ReadFromJsonAsync<InitialPipelinesDto>())!;
        Assert.Equal(4, pipelines.Pipelines.Count); Assert.Equal(28, pipelines.Pipelines.Sum(x => x.Stages.Count));
        var pipeline = pipelines.Pipelines.First(); var stage = pipeline.Stages.First();
        using var sourceResponse = await client.PostAsJsonAsync("/api/source-configurations", new CreateSourceConfigurationRequest { Name = "RSS", SourceTypeCode = "rss" });
        Assert.Equal(HttpStatusCode.Created, sourceResponse.StatusCode); var source = (await sourceResponse.Content.ReadFromJsonAsync<SourceConfigurationDto>())!;
        using var searchResponse = await client.PostAsJsonAsync("/api/saved-searches", new CreateSavedSearchRequest
        { Name = "Public feed", PipelineId = pipeline.Id, SourceConfigurationId = source.Id, SearchUrl = FeedUrl });
        Assert.Equal(HttpStatusCode.Created, searchResponse.StatusCode); var search = (await searchResponse.Content.ReadFromJsonAsync<SavedSearchDto>())!;
        var first = await Collect(client, search.Id, stage.Id);
        await Get(client, $"/api/source-executions/{first.Ingestion.Execution.Id}");
        await Get(client, $"/api/source-executions/{first.Ingestion.Execution.Id}/history");
        var page = await Get(client, $"/api/source-executions/{first.Ingestion.Execution.Id}/items"); Assert.Single(page.GetProperty("items").EnumerateArray());
        var second = await Collect(client, search.Id, stage.Id); Assert.Equal(1, second.Ingestion.Execution.ItemsUpdated);
        Assert.Equal(1, await db.Opportunities.CountAsync()); Assert.Equal(1, await db.OpportunitySources.CountAsync());
        Assert.Equal(2, await db.SourceExecutions.CountAsync()); Assert.Empty(await db.Companies.ToListAsync());
    }

    [Fact]
    public async Task FingerprintIsCheckedAfterWaitingForWorkspaceIdentityLock()
    {
        var setup = await RssSetup(); var fake = new FakeTransport(Rss()) { Gated = true };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var blocker = Db(); await using var transaction = await blocker.Database.BeginTransactionAsync(timeout.Token);
        await WorkspaceIdentityLock.AcquireAsync(blocker, setup.Workspace, timeout.Token);
        using var factory = Factory(fake); using var client = Client(factory);
        var pending = client.PostAsJsonAsync(Route(setup.Search), new { pipelineStageId = setup.Stage }, timeout.Token);
        await fake.Entered.Task.WaitAsync(timeout.Token); fake.Release.TrySetResult();
        await WaitForLocks(1, timeout.Token);
        try
        {
            using var changed = await client.PutAsJsonAsync($"/api/saved-searches/{setup.Search}", new UpdateSavedSearchRequest
            { Name = "Changed after network", PipelineId = setup.Pipeline, SourceConfigurationId = setup.Configuration, SearchUrl = FeedUrl }, timeout.Token);
            Assert.Equal(HttpStatusCode.NoContent, changed.StatusCode); Assert.False(pending.IsCompleted);
        }
        finally { await transaction.RollbackAsync(CancellationToken.None); }
        using var response = await pending;
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("CollectionConfigurationChanged", (await Json(response)).GetProperty("code").GetString());
        await using var db = Db(); Assert.Empty(await db.Opportunities.ToListAsync()); Assert.Empty(await db.SourceExecutionItems.ToListAsync());
    }

    [Fact]
    public async Task LockedFingerprintResourcesCannotChangeBeforeIngestionCommit()
    {
        var setup = await RssSetup(); var fake = new FakeTransport(Rss());
        var gate = new PersistentSourceIdentityGate(db => db.ChangeTracker.Entries<ProspectionCrm.Api.Entities.SourceExecution>()
            .Any(e => e.State == EntityState.Added && e.Entity.StatusCode == "running"));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var factory = Factory(fake, gate); using var client = Client(factory);
        var pending = client.PostAsJsonAsync(Route(setup.Search), new { pipelineStageId = setup.Stage }, timeout.Token);
        await gate.Reached.Task.WaitAsync(timeout.Token);
        var writes = new[] { Change("SavedSearches", setup.Search), Change("SourceConfigurations", setup.Configuration),
            Change("Pipelines", setup.Pipeline), Change("PipelineStages", setup.Stage) };
        try { await WaitForLocks(4, timeout.Token); Assert.All(writes, t => Assert.False(t.IsCompleted)); }
        finally { gate.Release.TrySetResult(); }
        using var response = await pending; Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        await Task.WhenAll(writes);
        var result = (await response.Content.ReadFromJsonAsync<SourceCollectionDto>())!;
        var history = await Get(client, $"/api/source-executions/{result.Ingestion.Execution.Id}/history");
        Assert.Equal("Search", history.GetProperty("contextSnapshot").GetProperty("savedSearch").GetProperty("name").GetString());
        Assert.Equal("Stage", history.GetProperty("contextSnapshot").GetProperty("targetStage").GetProperty("name").GetString());
        async Task Change(string table, Guid id)
        {
            await using var connection = new NpgsqlConnection(Postgres.GetConnectionString()); await connection.OpenAsync(timeout.Token);
            // Table is selected only from the fixed test literals above; the ID remains a parameter.
            await using var command = new NpgsqlCommand($"UPDATE \"{table}\" SET \"Name\" = 'Changed' WHERE \"Id\" = @id", connection);
            command.Parameters.AddWithValue("id", id); Assert.Equal(1, await command.ExecuteNonQueryAsync(timeout.Token));
        }
    }

    private async Task WaitForLocks(int count, CancellationToken token)
    {
        await using var observer = new NpgsqlConnection(Postgres.GetConnectionString()); await observer.OpenAsync(token);
        await using var command = new NpgsqlCommand("SELECT count(*) FROM pg_stat_activity WHERE datname = current_database() AND wait_event_type = 'Lock'", observer);
        while (Convert.ToInt32(await command.ExecuteScalarAsync(token)) < count) await Task.Delay(25, token);
    }

    [Fact]
    public async Task UnexpectedPreflightDatabaseFailureIsSanitizedWithoutNetworkOrExecution()
    {
        var setup = await RssSetup(); var fake = new FakeTransport(Rss());
        using var factory = Factory(fake, new BrokenPreflight()); using var client = Client(factory);
        using var response = await client.PostAsJsonAsync(Route(setup.Search), new { pipelineStageId = setup.Stage });
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var problem = await Json(response); Assert.Equal("CollectionInternalError", problem.GetProperty("code").GetString());
        Assert.Equal(JsonValueKind.Null, problem.GetProperty("executionId").ValueKind);
        Assert.DoesNotContain("sensitive", problem.GetRawText()); Assert.Equal(0, fake.Calls);
        await using var db = Db(); Assert.Empty(await db.SourceExecutions.ToListAsync());
    }

    private sealed class BrokenPreflight : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("SavedSearches", StringComparison.Ordinal))
                throw new InvalidOperationException("sensitive SQL and connection details");
            return ValueTask.FromResult(result);
        }
    }

    private sealed class FakeTransport(string xml) : IRssFeedTransport
    {
        public string Xml { get; set; } = xml;
        public Exception? Failure { get; set; }
        public bool Gated { get; init; }
        public int Calls { get; private set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<RssFeedResponse> FetchAsync(Uri uri, CancellationToken cancellationToken)
        {
            Calls++; Assert.Equal(FeedUrl, uri.AbsoluteUri); Entered.TrySetResult();
            if (Gated) await Release.Task.WaitAsync(cancellationToken);
            if (Failure is not null) throw Failure;
            return new(Encoding.UTF8.GetBytes(Xml), uri);
        }
    }
    private sealed class Connections : DbConnectionInterceptor
    {
        private int open;
        public int OpenCount => Volatile.Read(ref open);
        public override Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
        { Interlocked.Increment(ref open); return Task.CompletedTask; }
        public override Task ConnectionClosedAsync(DbConnection connection, ConnectionEndEventData eventData)
        { Interlocked.Decrement(ref open); return Task.CompletedTask; }
    }
}
