using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using ProspectionCrm.Api.Data;
using ProspectionCrm.Api.Dtos.CollectionJobs;
using ProspectionCrm.Api.Dtos.IngestionHistory;
using ProspectionCrm.Api.Dtos.Opportunities;
using ProspectionCrm.Api.Dtos.OpportunitySources;
using ProspectionCrm.Api.Dtos.SavedSearches;
using ProspectionCrm.Api.Dtos.Setup;
using ProspectionCrm.Api.Dtos.SourceConfigurations;
using ProspectionCrm.Api.Services.Collection;
using Testcontainers.PostgreSql;
using Xunit;

namespace ProspectionCrm.Api.Tests;

public sealed class Phase7ReconstructionTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer postgres = new PostgreSqlBuilder("postgres:18").Build();
    public Task InitializeAsync() => postgres.StartAsync();
    public Task DisposeAsync() => postgres.DisposeAsync().AsTask();
    private ProspectionCrmDbContext Db() => new(new DbContextOptionsBuilder<ProspectionCrmDbContext>()
        .UseNpgsql(postgres.GetConnectionString()).Options);
    private sealed class Feed : IRssFeedTransport
    {
        public int Calls;
        public Task<RssFeedResponse> FetchAsync(Uri uri, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Interlocked.Increment(ref Calls);
            return Task.FromResult(new RssFeedResponse(Encoding.UTF8.GetBytes(RssAtomFeedParserTests.Rss(
                "<item><title>Phase 7 Developer</title><guid>phase7-one</guid><link>https://jobs.example.org/phase7-one</link></item>")), uri));
        }
    }
    private static async Task<T> Get<T>(HttpClient client, string route)
    {
        using var response = await client.GetAsync(route); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }
    private static async Task<T> Post<T>(HttpClient client, string route, object? body, HttpStatusCode expected = HttpStatusCode.Created)
    {
        using var response = body is null ? await client.PostAsync(route, null) : await client.PostAsJsonAsync(route, body);
        Assert.Equal(expected, response.StatusCode); return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    [Fact]
    public async Task VirginDatabaseReconstructsScheduledCollectionThroughPublicConfigurationAndHistory()
    {
        await using var db = Db();
        await using (var connection = new NpgsqlConnection(postgres.GetConnectionString()))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand("SELECT count(*) FROM information_schema.tables WHERE table_schema = 'public'", connection);
            Assert.Equal(0L, await command.ExecuteScalarAsync());
        }
        Assert.Empty(await db.Database.GetAppliedMigrationsAsync());
        await db.Database.MigrateAsync();
        Assert.Empty(await db.Workspaces.ToListAsync()); Assert.Empty(await db.SourceCollectionSchedules.ToListAsync());
        Assert.Empty(await db.SourceCollectionJobs.ToListAsync());
        Assert.Empty(await db.Database.GetPendingMigrationsAsync()); Assert.False(db.Database.HasPendingModelChanges());
        var feed = new Feed();
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseEnvironment("Testing")
            .UseSetting("ConnectionStrings:DefaultConnection", postgres.GetConnectionString())
            .UseSetting("SourceCollectionWorker:Enabled", "false").UseSetting("SourceCollectionScheduler:Enabled", "false")
            .ConfigureServices(s => { s.RemoveAll<IRssFeedTransport>(); s.AddSingleton<IRssFeedTransport>(feed); }));
        using var client = factory.CreateClient(new() { BaseAddress = new("https://localhost") });
        var bootstrap = await Post<BootstrapDto>(client, "/api/setup/bootstrap", null);
        var initialized = await Post<InitialPipelinesDto>(client, "/api/setup/initial-pipelines", null);
        var pipeline = Assert.Single(initialized.Pipelines, x => x.Name == "Emploi .NET");
        var stage = pipeline.Stages.Where(x => x.CategoryCode == "active").OrderBy(x => x.SortOrder).First();
        const string secret = "phase7-reconstruction-private-token";
        var source = await Post<SourceConfigurationDto>(client, "/api/source-configurations", new
        { name = "RSS reconstruction", sourceTypeCode = "rss", enabled = true, configurationJson = "{\"token\":\"" + secret + "\"}" });
        var search = await Post<SavedSearchDto>(client, "/api/saved-searches", new
        { name = "Scheduled reconstruction", pipelineId = pipeline.Id, sourceConfigurationId = source.Id, searchUrl = "https://feeds.example.org/jobs.xml", criteriaJson = "{}", enabled = true });
        Assert.False(search.Schedule.Enabled);
        using (var response = await client.PutAsJsonAsync($"/api/saved-searches/{search.Id}/schedule",
            new { enabled = true, dailyUtcTime = "09:30", pipelineStageId = stage.Id })) Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var configured = await Get<SavedSearchDto>(client, $"/api/saved-searches/{search.Id}");
        Assert.True(configured.Schedule.Enabled); Assert.Equal(stage.Id, configured.Schedule.PipelineStageId);
        Assert.Equal("09:30", configured.Schedule.DailyUtcTime); Assert.True(configured.Schedule.NextCollectionAt > DateTimeOffset.UtcNow);
        Assert.Equal(0, feed.Calls); Assert.Empty(await db.SourceExecutions.ToListAsync());
        // Advance only the persisted due date; all business resources were created through HTTP.
        await db.SourceCollectionSchedules.Where(x => x.SavedSearchId == search.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.NextCollectionAt, DateTimeOffset.UtcNow.AddDays(-3)));
        await using (var schedulerScope = factory.Services.CreateAsyncScope())
            Assert.Equal(1, await schedulerScope.ServiceProvider.GetRequiredService<ISourceCollectionSchedulingService>().ScheduleDueAsync(default));
        var page = await Get<SourceCollectionJobsPageDto>(client, $"/api/source-collection-jobs?savedSearchId={search.Id}&triggerTypeCode=scheduled");
        var queued = Assert.Single(page.Items);
        Assert.Equal(bootstrap.WorkspaceId, queued.WorkspaceId); Assert.Equal(pipeline.Id, queued.PipelineId); Assert.Equal(stage.Id, queued.PipelineStageId);
        Assert.Equal("queued", queued.StatusCode); Assert.Equal("scheduled", queued.TriggerTypeCode); Assert.Equal(0, queued.AttemptCount);
        Assert.Equal(0, feed.Calls); Assert.Empty(await db.SourceExecutions.ToListAsync());
        var next = (await Get<SavedSearchDto>(client, $"/api/saved-searches/{search.Id}")).Schedule.NextCollectionAt;
        Assert.True(next > DateTimeOffset.UtcNow);
        await using (var workerScope = factory.Services.CreateAsyncScope())
            Assert.True(await workerScope.ServiceProvider.GetRequiredService<ISourceCollectionJobProcessor>().ProcessNextAsync(default));
        var finished = await Get<SourceCollectionJobDto>(client, $"/api/source-collection-jobs/{queued.Id}");
        Assert.Equal("succeeded", finished.StatusCode); Assert.Equal(1, finished.AttemptCount); Assert.NotNull(finished.SourceExecutionId);
        Assert.Equal("scheduled", finished.TriggerTypeCode); Assert.Equal(1, feed.Calls);
        var attempt = await db.SourceCollectionJobAttempts.AsNoTracking().SingleAsync();
        Assert.Equal(finished.Id, attempt.JobId); Assert.Equal(bootstrap.WorkspaceId, attempt.WorkspaceId);
        Assert.Equal("succeeded", attempt.StatusCode); Assert.Equal(finished.SourceExecutionId, attempt.SourceExecutionId);
        var execution = await db.SourceExecutions.AsNoTracking().SingleAsync();
        Assert.Equal(bootstrap.WorkspaceId, execution.WorkspaceId);
        Assert.Equal(pipeline.Id, execution.TargetPipelineId); Assert.Equal(stage.Id, execution.TargetPipelineStageId);
        var history = await Get<SourceExecutionHistoryDto>(client, $"/api/source-executions/{finished.SourceExecutionId}/history");
        Assert.Equal("scheduled", history.Execution.TriggerTypeCode); Assert.Equal("succeeded", history.Execution.StatusCode);
        Assert.True(history.Execution.HistoryAvailable); Assert.DoesNotContain(secret, JsonSerializer.Serialize(history));
        var items = await Get<SourceExecutionItemsPageDto>(client, $"/api/source-executions/{finished.SourceExecutionId}/items");
        var item = Assert.Single(items.Items); Assert.Equal("created", item.OutcomeCode); Assert.NotNull(item.OpportunityId);
        var opportunity = await Get<OpportunityDto>(client, $"/api/opportunities/{item.OpportunityId}");
        Assert.Equal(stage.Id, opportunity.PipelineStageId); Assert.Equal("Phase 7 Developer", opportunity.Title);
        var provenance = Assert.Single(await Get<OpportunitySourceDto[]>(client, $"/api/opportunities/{opportunity.Id}/sources"));
        Assert.Equal(bootstrap.WorkspaceId, provenance.WorkspaceId); Assert.Equal(source.Id, provenance.SourceConfigurationId);
        Assert.Equal(search.Id, provenance.SavedSearchId); Assert.Equal(finished.SourceExecutionId, provenance.SourceExecutionId);
        Assert.Equal("phase7-one", provenance.ExternalId); Assert.Equal(2, item.Sources.Count);
        Assert.All(item.Sources, x => Assert.Equal(provenance.Id, x.OpportunitySourceId));
        Assert.Equal(next, (await Get<SavedSearchDto>(client, $"/api/saved-searches/{search.Id}")).Schedule.NextCollectionAt);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<ISourceCollectionSchedulingService>().ScheduleDueAsync(default));
            Assert.False(await scope.ServiceProvider.GetRequiredService<ISourceCollectionJobProcessor>().ProcessNextAsync(default));
        }
        Assert.Single(await db.SourceCollectionJobs.ToListAsync()); Assert.Single(await db.Opportunities.ToListAsync()); Assert.Equal(1, feed.Calls);
        Assert.Empty(await db.Database.GetPendingMigrationsAsync()); Assert.False(db.Database.HasPendingModelChanges());
    }
}
