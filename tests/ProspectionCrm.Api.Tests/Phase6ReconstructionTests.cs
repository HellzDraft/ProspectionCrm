using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using ProspectionCrm.Api.Data;
using ProspectionCrm.Api.Dtos.IngestionHistory;
using ProspectionCrm.Api.Dtos.Ingestions;
using ProspectionCrm.Api.Dtos.Opportunities;
using ProspectionCrm.Api.Dtos.OpportunitySources;
using ProspectionCrm.Api.Dtos.SavedSearches;
using ProspectionCrm.Api.Dtos.Setup;
using ProspectionCrm.Api.Dtos.SourceConfigurations;
using ProspectionCrm.Api.Dtos.SourceExecutions;
using ProspectionCrm.Api.Entities;
using Testcontainers.PostgreSql;
using Xunit;

namespace ProspectionCrm.Api.Tests;

public sealed class Phase6ReconstructionTests : IAsyncLifetime
{
    private const string LastMigration = "20261007124044_Phase81AutomationRuntimeSettings";
    private readonly PostgreSqlContainer postgres = new PostgreSqlBuilder("postgres:18").Build();
    public Task InitializeAsync() => postgres.StartAsync();
    public Task DisposeAsync() => postgres.DisposeAsync().AsTask();
    private ProspectionCrmDbContext Db() => new(new DbContextOptionsBuilder<ProspectionCrmDbContext>()
        .UseNpgsql(postgres.GetConnectionString()).Options);
    private static string Execution(Guid id) => $"/api/source-executions/{id}";
    private static string Opportunity(Guid id) => $"/api/opportunities/{id}";
    private static string Sources(Guid id) => Opportunity(id) + "/sources";
    private static async Task<T> Get<T>(HttpClient client, string route)
    {
        using var response = await client.GetAsync(route); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }
    private static async Task<T> Post<T>(HttpClient client, string route, object? body, HttpStatusCode status = HttpStatusCode.Created)
    {
        using var response = body is null ? await client.PostAsync(route, null) : await client.PostAsJsonAsync(route, body);
        Assert.Equal(status, response.StatusCode); return (await response.Content.ReadFromJsonAsync<T>())!;
    }
    private static void SameJson(object expected, object actual) => Assert.True(JsonNode.DeepEquals(
        JsonSerializer.SerializeToNode(expected), JsonSerializer.SerializeToNode(actual)));
    private static IngestionItemRequest Item(string external, string url, string title) => new() { ExternalId = external, SourceUrl = url, Title = title };

    [Fact]
    public async Task FreshPostgresReconstructsCollectionThroughHttpAndPreservesHistoricalSnapshots()
    {
        await using var db = Db();
        await using (var connection = new NpgsqlConnection(postgres.GetConnectionString()))
        {
            await connection.OpenAsync();
            await using var tables = new NpgsqlCommand("SELECT count(*) FROM information_schema.tables WHERE table_schema = 'public'", connection);
            Assert.Equal(0L, await tables.ExecuteScalarAsync());
        }
        var declared = db.Database.GetMigrations().ToArray(); Assert.NotEmpty(declared);
        Assert.Empty(await db.Database.GetAppliedMigrationsAsync());
        Assert.Equal(declared, await db.Database.GetPendingMigrationsAsync());
        await db.Database.MigrateAsync();
        Assert.Equal(declared, await db.Database.GetAppliedMigrationsAsync());
        Assert.Empty(await db.Database.GetPendingMigrationsAsync()); Assert.False(db.Database.HasPendingModelChanges());
        Assert.Equal(LastMigration, declared.Last());

        // Testing avoids development User Secrets; this connection is the disposable container only.
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseEnvironment("Testing")
            .UseSetting("ConnectionStrings:DefaultConnection", postgres.GetConnectionString()));
        using var client = factory.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
        var bootstrap = await Post<BootstrapDto>(client, "/api/setup/bootstrap", null);
        Assert.Equal(bootstrap.OwnerUserId, (await db.UserAccounts.AsNoTracking().SingleAsync()).Id);
        var workspace = await db.Workspaces.AsNoTracking().SingleAsync();
        Assert.Equal(bootstrap.WorkspaceId, workspace.Id); Assert.Equal(bootstrap.OwnerUserId, workspace.OwnerUserId);
        Assert.Equal(bootstrap, await Post<BootstrapDto>(client, "/api/setup/bootstrap", null, HttpStatusCode.OK));
        Assert.Equal(1, await db.UserAccounts.CountAsync()); Assert.Equal(1, await db.Workspaces.CountAsync());
        var initial = await Post<InitialPipelinesDto>(client, "/api/setup/initial-pipelines", null);
        Assert.Equal(4, initial.Pipelines.Count); Assert.Equal(4, initial.CreatedPipelineIds.Count);
        Assert.Equal(28, initial.Pipelines.Sum(x => x.Stages.Count));
        var pipeline = Assert.Single(initial.Pipelines, x => x.Name == "Emploi .NET"); Assert.True(pipeline.IsDefault);
        Assert.Equal(pipeline.Id, await db.Workspaces.Select(x => x.DefaultPipelineId).SingleAsync());
        var repeated = await Post<InitialPipelinesDto>(client, "/api/setup/initial-pipelines", null, HttpStatusCode.OK);
        Assert.Empty(repeated.CreatedPipelineIds); SameJson(initial.Pipelines, repeated.Pipelines);
        Assert.Equal(4, await db.Pipelines.CountAsync()); Assert.Equal(28, await db.PipelineStages.CountAsync());
        var stages = pipeline.Stages.Where(x => x.ArchivedAt is null && x.CategoryCode == "active").OrderBy(x => x.SortOrder).ToArray();
        Assert.True(stages.Length >= 2); var stage = stages[0];
        const string secret = "phase6-fake-secret-never-in-history";
        var source = await Post<SourceConfigurationDto>(client, "/api/source-configurations", new CreateSourceConfigurationRequest
            { Name = "Reconstruction", SourceTypeCode = "manual", Enabled = true, ConfigurationJson = "{\"token\":\"" + secret + "\"}" });
        Assert.NotEqual(Guid.Empty, source.Id); Assert.True(source.Enabled); Assert.Null(source.ArchivedAt); Assert.Equal("manual", source.SourceTypeCode);
        var search = await Post<SavedSearchDto>(client, "/api/saved-searches", new CreateSavedSearchRequest
            { Name = "Reconstruction", PipelineId = pipeline.Id, SourceConfigurationId = source.Id, CriteriaJson = "{\"keywords\":[\"dotnet\"],\"remote\":true}", Enabled = true });
        Assert.Equal(pipeline.Id, search.PipelineId); Assert.Equal(source.Id, search.SourceConfigurationId); Assert.True(search.Enabled); Assert.Null(search.ArchivedAt);
        var route = $"/api/saved-searches/{search.Id}/ingestions";
        var firstInput = new IngestionItemRequest { Title = "  Dotnet\tDeveloper ", CompanyName = " Studio  A ", ExternalId = "first",
            SourceUrl = " HTTPS://EXAMPLE.INVALID/Jobs/First?b=2&a=1#Top ", Location = " Bordeaux ", Description = " Original description " };
        var secondInput = Item("second", "https://example.invalid/Jobs/Second", "Backend Developer");
        async Task<IngestionDto> Ingest(params IngestionItemRequest[] items) => await Post<IngestionDto>(client, route,
            new IngestionRequest { PipelineStageId = stage.Id, Items = items.ToList() });
        var first = await Ingest(firstInput, secondInput, firstInput);
        Assert.Equal("succeeded", first.Execution.StatusCode);
        Assert.Equal((3, 2, 0, 1, 0, 0, 0, 0), Counters(first.Execution));
        Assert.Equal(new[] { "created", "created", "ignored" }, first.Items.Select(x => x.Outcome));
        var firstId = first.Items[0].OpportunityId; var secondId = first.Items[1].OpportunityId;
        Assert.Equal(firstId, first.Items[2].OpportunityId); Assert.NotEqual(firstId, secondId);
        Assert.Equal(2, await db.Opportunities.CountAsync()); Assert.Equal(0, await db.Companies.CountAsync());
        var sourceRows = await db.OpportunitySources.AsNoTracking().OrderBy(x => x.Id).ToArrayAsync(); Assert.Equal(2, sourceRows.Length);
        Assert.Equal(2, sourceRows.Select(x => x.NormalizedSourceUrl).Distinct().Count());
        Assert.All(sourceRows, x => { Assert.Equal(workspace.Id, x.WorkspaceId); Assert.NotNull(x.NormalizedSourceUrl); Assert.NotNull(x.ExternalId); });
        var firstSource = Assert.Single(sourceRows, x => x.OpportunityId == firstId);
        Assert.Equal("https://example.invalid/Jobs/First?b=2&a=1#Top", firstSource.NormalizedSourceUrl);
        var original = await Get<OpportunityDto>(client, Opportunity(firstId));
        Assert.Null(original.CompanyId); Assert.Null(original.ContactId); Assert.Null(original.Score); Assert.Null(original.ScoredAt);
        Assert.Equal(stage.Id, original.PipelineStageId); Assert.Equal("normal", original.PriorityCode); Assert.Null(original.ArchivedAt);
        Assert.Equal(firstInput.Title, original.Title); Assert.Equal(firstInput.Location, original.Location); Assert.Equal(firstInput.Description, original.Notes);

        var history = await Get<SourceExecutionHistoryDto>(client, Execution(first.Execution.Id) + "/history");
        SameJson(first.Execution, history.Execution); SameJson(first.Execution, await Get<SourceExecutionDto>(client, Execution(first.Execution.Id)));
        Assert.True(history.Execution.HistoryAvailable); Assert.Equal(JsonValueKind.Object, history.ContextSnapshot!.Value.ValueKind);
        Assert.Equal(JsonValueKind.Object, history.ContextSnapshot.Value.GetProperty("savedSearch").GetProperty("criteria").ValueKind);
        var historyJson = JsonSerializer.Serialize(history); Assert.DoesNotContain(secret, historyJson); Assert.DoesNotContain("configurationJson", historyJson, StringComparison.OrdinalIgnoreCase);
        var firstPage = await Get<SourceExecutionItemsPageDto>(client, Execution(first.Execution.Id) + "/items");
        Assert.Equal(new[] { 0, 1, 2 }, firstPage.Items.Select(x => x.ItemIndex)); Assert.Equal(new[] { "created", "created", "ignored" }, firstPage.Items.Select(x => x.OutcomeCode));
        Assert.All(firstPage.Items, x => { Assert.Equal(x.OpportunityId, x.OpportunityIdSnapshot); Assert.Equal(JsonValueKind.Object, x.DecisionDetails!.Value.ValueKind); });
        var observed = firstPage.Items[0]; Assert.Equal(firstInput.Title, observed.Title); Assert.Equal("DOTNET DEVELOPER", observed.NormalizedTitle);
        Assert.Equal(firstInput.CompanyName, observed.CompanyName); Assert.Equal("STUDIO A", observed.NormalizedCompanyName);
        Assert.Equal(firstInput.SourceUrl, observed.SourceUrl); Assert.Equal(firstSource.NormalizedSourceUrl, observed.NormalizedSourceUrl);
        Assert.Equal(JsonValueKind.Object, observed.PayloadSnapshot!.Value.ValueKind);
        Assert.Equal(firstInput.Location, observed.PayloadSnapshot.Value.GetProperty("location").GetString());
        Assert.Equal(firstInput.Description, observed.PayloadSnapshot.Value.GetProperty("description").GetString());
        foreach (var item in firstPage.Items.Take(2))
        {
            Assert.Equal(new[] { "external-id", "source-url" }, item.Sources.Select(x => x.RoleCode));
            Assert.All(item.Sources, link => Assert.Equal(link.OpportunitySourceId, link.OpportunitySourceIdSnapshot));
        }
        Assert.Empty(firstPage.Items[2].Sources);

        var replay = await Ingest(firstInput, new IngestionItemRequest { Title = "dotnet developer", CompanyName = "studio a" });
        Assert.NotEqual(first.Execution.Id, replay.Execution.Id); Assert.Equal((2, 0, 2, 0, 0, 0, 0, 0), Counters(replay.Execution));
        Assert.All(replay.Items, x => Assert.Equal(firstId, x.OpportunityId));
        var replayPage = await Get<SourceExecutionItemsPageDto>(client, Execution(replay.Execution.Id) + "/items");
        Assert.Equal("MatchedTitleCompany", replayPage.Items[1].DecisionCode); Assert.Empty(replayPage.Items[1].Sources);
        Assert.DoesNotContain(replayPage.Items, x => firstPage.Items.Any(y => y.Id == x.Id));
        Assert.Equal(2, await db.Opportunities.CountAsync()); Assert.Equal(2, await db.OpportunitySources.CountAsync());
        Assert.True(await db.OpportunitySources.Where(x => x.Id == firstSource.Id).Select(x => x.LastSeenAt > firstSource.LastSeenAt).SingleAsync());
        SameJson(original, await Get<OpportunityDto>(client, Opportunity(firstId)));
        using (var edited = await client.PutAsJsonAsync(Opportunity(firstId), new UpdateOpportunityRequest
            { Title = "User title", PriorityCode = "high", Notes = "User notes", Location = "User location", PipelineStageId = stages[1].Id }))
            Assert.Equal(HttpStatusCode.NoContent, edited.StatusCode);
        using (var archived = await client.PostAsync(Opportunity(firstId) + "/archive", null)) Assert.Equal(HttpStatusCode.NoContent, archived.StatusCode);
        var userVersion = await Get<OpportunityDto>(client, Opportunity(firstId)); Assert.NotNull(userVersion.ArchivedAt);
        var archivedReplay = await Ingest(firstInput); Assert.Equal(firstId, archivedReplay.Items[0].OpportunityId); Assert.Equal("updated", archivedReplay.Items[0].Outcome);
        SameJson(userVersion, await Get<OpportunityDto>(client, Opportunity(firstId)));

        var beforeConflict = await Snapshot("Opportunities", "OpportunitySources", "Companies");
        using var conflict = await client.PostAsJsonAsync(route, new IngestionRequest { PipelineStageId = stage.Id, Items =
            [Item("provisional", "https://example.invalid/provisional", "Provisional"), Item("first", secondInput.SourceUrl!, "Contradiction"), Item("not-processed", "https://example.invalid/not-processed", "Unprocessed")] });
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        var problemText = await conflict.Content.ReadAsStringAsync(); var problem = JsonSerializer.Deserialize<JsonElement>(problemText);
        Assert.Equal("AmbiguousIdentity", problem.GetProperty("code").GetString()); Assert.Equal(1, problem.GetProperty("itemIndex").GetInt32());
        Assert.DoesNotContain("Npgsql", problemText); Assert.DoesNotContain("23505", problemText); Assert.DoesNotContain("SELECT", problemText);
        var failedId = problem.GetProperty("executionId").GetGuid();
        var failed = await Get<SourceExecutionDto>(client, Execution(failedId)); Assert.Equal("failed", failed.StatusCode);
        Assert.Equal((3, 0, 0, 0, 1, 1, 1, 0), Counters(failed));
        var failedPage = await Get<SourceExecutionItemsPageDto>(client, Execution(failedId) + "/items");
        Assert.Equal(new[] { "rolled-back", "rejected", "not-processed" }, failedPage.Items.Select(x => x.OutcomeCode));
        Assert.All(failedPage.Items, x => { Assert.Null(x.OpportunityId); Assert.Null(x.OpportunityIdSnapshot); Assert.Empty(x.Sources); });
        Assert.Equal(beforeConflict, await Snapshot("Opportunities", "OpportunitySources", "Companies"));
        Assert.False(await db.SourceExecutionItems.AnyAsync(x => x.OutcomeCode == "pending"));
        Assert.Equal(2, await db.Opportunities.CountAsync()); Assert.Equal(2, await db.OpportunitySources.CountAsync());

        var page0 = await Get<SourceExecutionItemsPageDto>(client, Execution(first.Execution.Id) + "/items?limit=2");
        var page1 = await Get<SourceExecutionItemsPageDto>(client, Execution(first.Execution.Id) + "/items?limit=2&offset=2");
        Assert.Equal(3, page0.TotalCount); Assert.True(page0.HasMore); Assert.Equal(3, page1.TotalCount); Assert.False(page1.HasMore);
        Assert.Equal(firstPage.Items.Select(x => x.Id), page0.Items.Concat(page1.Items).Select(x => x.Id));
        var rejected = await Get<SourceExecutionItemsPageDto>(client, Execution(failedId) + "/items?outcomeCode=rejected&decisionCode=AmbiguousIdentity");
        Assert.Equal(1, rejected.TotalCount); Assert.Equal(1, Assert.Single(rejected.Items).ItemIndex);
        var observationsRoute = Opportunity(firstId) + "/observations";
        var observations = await Get<OpportunityObservationsPageDto>(client, observationsRoute); Assert.Equal(5, observations.TotalCount);
        var expected = await db.SourceExecutionItems.Where(x => x.OpportunityId == firstId).OrderByDescending(x => x.ReceivedAt).ThenByDescending(x => x.Id).Select(x => x.Id).ToArrayAsync();
        Assert.Equal(expected, observations.Items.Select(x => x.Id));
        Assert.Equal(5, (await Get<OpportunityObservationsPageDto>(client, observationsRoute + $"?sourceConfigurationId={source.Id}&savedSearchId={search.Id}")).TotalCount);
        var instant = Uri.EscapeDataString(first.Execution.StartedAt.ToOffset(TimeSpan.FromHours(2)).ToString("O"));
        Assert.Equal(2, (await Get<OpportunityObservationsPageDto>(client, observationsRoute + $"?from={instant}&to={instant}")).TotalCount);
        var sourceRoute = Sources(firstId) + $"/{firstSource.Id}/observations";
        var sourcePage = await Get<OpportunitySourceObservationsPageDto>(client, sourceRoute); Assert.Equal(3, sourcePage.TotalCount);
        Assert.Equal(3, sourcePage.Items.Select(x => x.Id).Distinct().Count()); Assert.All(sourcePage.Items, x => Assert.Equal(2, x.Sources.Count));
        foreach (var role in new[] { "external-id", "source-url" })
        {
            var ids = new List<Guid>();
            for (var offset = 0; offset < 3; offset++)
            {
                var part = await Get<OpportunitySourceObservationsPageDto>(client, sourceRoute + $"?roleCode={role}&offset={offset}&limit=1");
                Assert.Equal(3, part.TotalCount); Assert.Equal(offset < 2, part.HasMore); Assert.Equal(2, Assert.Single(part.Items).Sources.Count); ids.Add(part.Items[0].Id);
            }
            Assert.Equal(sourcePage.Items.Select(x => x.Id), ids);
        }
        var itemCount = await db.SourceExecutionItems.CountAsync(); var linkCount = await db.SourceExecutionItemSources.CountAsync();
        using (var deletion = await client.DeleteAsync(Opportunity(firstId))) Assert.Equal(HttpStatusCode.NoContent, deletion.StatusCode);
        using (var missing = await client.GetAsync(observationsRoute)) Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        using (var missing = await client.GetAsync(sourceRoute)) Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        foreach (var execution in new[] { first.Execution.Id, replay.Execution.Id, archivedReplay.Execution.Id })
        {
            var page = await Get<SourceExecutionItemsPageDto>(client, Execution(execution) + "/items");
            foreach (var item in page.Items.Where(x => x.OpportunityIdSnapshot == firstId))
            {
                Assert.Null(item.OpportunityId); Assert.Equal(firstId, item.OpportunityIdSnapshot);
                Assert.All(item.Sources, x => { Assert.Null(x.OpportunitySourceId); Assert.Equal(firstSource.Id, x.OpportunitySourceIdSnapshot); });
            }
        }
        Assert.Equal(itemCount, await db.SourceExecutionItems.CountAsync()); Assert.Equal(linkCount, await db.SourceExecutionItemSources.CountAsync());
        Assert.Equal(1, await db.Opportunities.CountAsync()); Assert.Equal(1, await db.OpportunitySources.CountAsync());
        var foreign = await SeedForeign();
        var beforeReads = await Snapshot();
        foreach (var path in new[] { Execution(foreign.Execution) + "/history", Execution(foreign.Execution) + "/items", Opportunity(foreign.Opportunity) + "/observations" })
        {
            using var response = await client.GetAsync(path); Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.DoesNotContain("foreign-only", await response.Content.ReadAsStringAsync());
        }
        Assert.Empty((await Get<OpportunityObservationsPageDto>(client, Opportunity(secondId) + $"/observations?sourceConfigurationId={foreign.Configuration}")).Items);
        await Get<SourceExecutionHistoryDto>(client, Execution(first.Execution.Id) + "/history");
        await Get<SourceExecutionItemsPageDto>(client, Execution(first.Execution.Id) + "/items");
        await Get<SourceExecutionHistoryDto>(client, Execution(failedId) + "/history");
        await Get<OpportunityObservationsPageDto>(client, Opportunity(secondId) + "/observations");
        var remainingSource = Assert.Single(await Get<OpportunitySourceDto[]>(client, Sources(secondId)));
        await Get<OpportunitySourceObservationsPageDto>(client, Sources(secondId) + $"/{remainingSource.Id}/observations");
        Assert.Equal(beforeReads, await Snapshot());
        Assert.Equal(LastMigration, (await db.Database.GetAppliedMigrationsAsync()).Last());
        Assert.Empty(await db.Database.GetPendingMigrationsAsync()); Assert.False(db.Database.HasPendingModelChanges());
    }

    private static (int, int, int, int, int, int, int, int) Counters(SourceExecutionDto x) =>
        (x.ItemsFound, x.ItemsCreated, x.ItemsUpdated, x.ItemsIgnored, x.ItemsRejected, x.ItemsRolledBack, x.ItemsNotProcessed, x.ItemsCancelled);

    private async Task<string> Snapshot(params string[] selectedTables)
    {
        await using var connection = new NpgsqlConnection(postgres.GetConnectionString()); await connection.OpenAsync();
        var tables = selectedTables.ToList();
        if (tables.Count == 0)
        {
            await using var command = new NpgsqlCommand("SELECT tablename FROM pg_tables WHERE schemaname = 'public' ORDER BY tablename", connection);
            await using var reader = await command.ExecuteReaderAsync(); while (await reader.ReadAsync()) tables.Add(reader.GetString(0));
        }
        using var identifiers = new NpgsqlCommandBuilder(); var rows = new List<string>();
        foreach (var table in tables)
        {
            await using var command = new NpgsqlCommand("SELECT COALESCE(jsonb_agg(to_jsonb(t) ORDER BY to_jsonb(t)::text), '[]'::jsonb)::text FROM " + identifiers.QuoteIdentifier(table) + " t", connection);
            rows.Add(table + ":" + (string)(await command.ExecuteScalarAsync())!);
        }
        return string.Join("\n", rows);
    }

    private async Task<(Guid Execution, Guid Opportunity, Guid Configuration)> SeedForeign()
    {
        await using var db = Db(); var now = DateTimeOffset.UtcNow;
        var workspace = new Workspace { Name = "Foreign", TimeZoneId = "UTC", ArchivedAt = now, OwnerUser = new UserAccount { Email = "foreign@example.invalid" } };
        var pipeline = new Pipeline { Workspace = workspace, Name = "Foreign", TypeCode = "custom" };
        var stage = new PipelineStage { Pipeline = pipeline, Name = "Foreign", CategoryCode = "active" };
        var source = new SourceConfiguration { Workspace = workspace, Name = "Foreign", SourceTypeCode = "manual" };
        var opportunity = new Opportunity { Workspace = workspace, PipelineStage = stage, Title = "foreign-only", PriorityCode = "normal" };
        var execution = new SourceExecution { Workspace = workspace, SourceConfiguration = source, TriggerTypeCode = "manual", StatusCode = "succeeded",
            StartedAt = now, FinishedAt = now, HistoryVersion = 1, ContractVersion = 1, NormalizationVersion = 1,
            TargetPipelineId = pipeline.Id, TargetPipelineStageId = stage.Id, ContextSnapshotJson = "{\"marker\":\"foreign-only\"}", ItemsFound = 1, ItemsCreated = 1 };
        execution.Items.Add(new SourceExecutionItem { WorkspaceId = workspace.Id, SourceExecution = execution, ItemIndex = 0,
            Opportunity = opportunity, OpportunityIdSnapshot = opportunity.Id, Title = "foreign-only", NormalizedTitle = "FOREIGN-ONLY",
            OutcomeCode = "created", DecisionCode = "CreatedNewOpportunity", ReceivedAt = now, ProcessedAt = now });
        db.SourceExecutions.Add(execution); await db.SaveChangesAsync(); return (execution.Id, opportunity.Id, source.Id);
    }
}
