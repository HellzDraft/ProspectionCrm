using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using ProspectionCrm.Api.Dtos.IngestionHistory;
using ProspectionCrm.Api.Dtos.Ingestions;
using ProspectionCrm.Api.Entities;
using ProspectionCrm.Api.Services;
using Xunit;

namespace ProspectionCrm.Api.Tests;

public sealed class IngestionHistoryReadTests : PersistentSourceIdentityFixture
{
    private WebApplicationFactory<Program> Factory() => new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        b.UseEnvironment("Testing").UseSetting("ConnectionStrings:DefaultConnection", Postgres.GetConnectionString()));
    private static HttpClient Client(WebApplicationFactory<Program> factory) => factory.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
    private static string Execution(Guid id) => $"/api/source-executions/{id}";
    private static string Opportunity(Guid id) => $"/api/opportunities/{id}/observations";
    private static string Source(Guid opportunity, Guid source) => $"/api/opportunities/{opportunity}/sources/{source}/observations";
    private static async Task<T> Get<T>(HttpClient client, string route)
    {
        using var response = await client.GetAsync(route);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }
    private static void SameJson(object expected, object actual) => Assert.True(JsonNode.DeepEquals(
        JsonSerializer.SerializeToNode(expected), JsonSerializer.SerializeToNode(actual)));

    [Fact]
    public async Task ContextIsRecordedJsonAndExistingContractsRemainUnchanged()
    {
        var setup = await PrepareAsync();
        await using var db = Db();
        await db.SourceConfigurations.Where(x => x.Id == setup.Configuration).ExecuteUpdateAsync(s => s
            .SetProperty(x => x.ConfigurationJson, "{\"token\":\"never-expose-secret\"}"));
        await db.SavedSearches.Where(x => x.Id == setup.Search).ExecuteUpdateAsync(s => s.SetProperty(x => x.CriteriaJson, "{\"keywords\":[\"Unity\"]}"));
        using var factory = Factory(); using var client = Client(factory);
        using var post = await client.PostAsJsonAsync($"/api/saved-searches/{setup.Search}/ingestions",
            new IngestionRequest { PipelineStageId = setup.Stage, Items = [Item()] });
        Assert.Equal(HttpStatusCode.Created, post.StatusCode);
        var original = (await post.Content.ReadFromJsonAsync<IngestionDto>())!;
        var history = await Get<SourceExecutionHistoryDto>(client, Execution(original.Execution.Id) + "/history");
        SameJson(original.Execution, history.Execution);
        SameJson(history.Execution, await Get<ProspectionCrm.Api.Dtos.SourceExecutions.SourceExecutionDto>(client, Execution(original.Execution.Id)));
        Assert.True(history.Execution.HistoryAvailable); Assert.Null(history.UnavailableReason);
        Assert.Equal(JsonValueKind.Object, history.ContextSnapshot!.Value.ValueKind);
        var contextText = history.ContextSnapshot.Value.GetRawText();
        Assert.DoesNotContain("never-expose-secret", contextText); Assert.DoesNotContain("configurationJson", contextText);
        using var stored = JsonDocument.Parse((await db.SourceExecutions.AsNoTracking().SingleAsync(x => x.Id == original.Execution.Id)).ContextSnapshotJson!);
        SameJson(stored.RootElement, history.ContextSnapshot.Value);
        var searchSnapshot = history.ContextSnapshot.Value.GetProperty("savedSearch");
        Assert.Equal(JsonValueKind.Object, searchSnapshot.GetProperty("criteria").ValueKind);
        await db.SavedSearches.Where(x => x.Id == setup.Search).ExecuteUpdateAsync(s => s.SetProperty(x => x.Name, "Changed").SetProperty(x => x.CriteriaJson, "{}"));
        await db.SourceConfigurations.Where(x => x.Id == setup.Configuration).ExecuteUpdateAsync(s => s.SetProperty(x => x.Name, "Changed"));
        await db.Pipelines.Where(x => x.Id == setup.Pipeline).ExecuteUpdateAsync(s => s.SetProperty(x => x.Name, "Changed"));
        await db.PipelineStages.Where(x => x.Id == setup.Stage).ExecuteUpdateAsync(s => s.SetProperty(x => x.Name, "Changed"));
        SameJson(history, await Get<SourceExecutionHistoryDto>(client, Execution(original.Execution.Id) + "/history"));
        var oldContract = await client.GetStringAsync(Execution(original.Execution.Id));
        Assert.DoesNotContain("contextSnapshot", oldContract, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("contextSnapshot", await post.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RealLegacyUpgradeReturnsAvailableResourceWithoutFabricatingHistory()
    {
        await using var db = Db();
        await db.GetService<IMigrator>().MigrateAsync("20260929150147_Phase42PipelineLifecycleAndDefault");
        var user = Guid.NewGuid(); var workspace = Guid.NewGuid(); var configuration = Guid.NewGuid(); var execution = Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "UserAccounts" ("Id", "Email", "CreatedAt") VALUES ({user}, 'legacy@example.invalid', now());
            INSERT INTO "Workspaces" ("Id", "OwnerUserId", "Name", "TimeZoneId", "CreatedAt") VALUES ({workspace}, {user}, 'Legacy', 'UTC', now());
            INSERT INTO "SourceConfigurations" ("Id", "WorkspaceId", "Name", "SourceTypeCode", "Enabled", "CreatedAt")
                VALUES ({configuration}, {workspace}, 'Legacy', 'manual', true, now());
            INSERT INTO "SourceExecutions" ("Id", "WorkspaceId", "SourceConfigurationId", "TriggerTypeCode", "StatusCode", "StartedAt",
                "ItemsFound", "ItemsCreated", "ItemsUpdated", "ItemsIgnored") VALUES ({execution}, {workspace}, {configuration}, 'manual', 'succeeded', now(), 3, 1, 1, 1);
            """);
        await db.Database.MigrateAsync();
        using var factory = Factory(); using var client = Client(factory);
        var history = await Get<SourceExecutionHistoryDto>(client, Execution(execution) + "/history");
        Assert.False(history.Execution.HistoryAvailable); Assert.Null(history.ContextSnapshot); Assert.Equal("legacy-execution", history.UnavailableReason);
        Assert.Equal(3, history.Execution.ItemsFound);
        var items = await Get<SourceExecutionItemsPageDto>(client, Execution(execution) + "/items");
        Assert.False(items.HistoryAvailable); Assert.Equal(0, items.TotalCount); Assert.Empty(items.Items); Assert.False(items.HasMore);
        Assert.False(db.Database.HasPendingModelChanges()); Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.Equal("20261008124357_Phase84AutomationExecutionRuntime", (await db.Database.GetAppliedMigrationsAsync()).Last());
    }

    [Fact]
    public async Task SuccessItemsPreserveRawValuesJsonSourcesAndExecutionProjection()
    {
        var setup = await PrepareAsync();
        var input = new IngestionItemRequest { ExternalId = " Exact ", SourceUrl = " HTTPS://EXAMPLE.INVALID/Job?B=2&a=1#F ",
            Title = " Unity\tDeveloper ", CompanyName = " Studio  A ", Description = " Raw description " };
        var ingested = (await IngestAsync(setup, input, input)).Value!;
        using var factory = Factory(); using var client = Client(factory);
        var page = await Get<SourceExecutionItemsPageDto>(client, Execution(ingested.Execution.Id) + "/items");
        Assert.True(page.HistoryAvailable); Assert.Equal((0, 50, 2, false), (page.Offset, page.Limit, page.TotalCount, page.HasMore));
        Assert.Equal(new[] { 0, 1 }, page.Items.Select(x => x.ItemIndex));
        var item = page.Items[0];
        Assert.Equal(input.Title, item.Title); Assert.Equal("UNITY DEVELOPER", item.NormalizedTitle);
        Assert.Equal(input.CompanyName, item.CompanyName); Assert.Equal("STUDIO A", item.NormalizedCompanyName);
        Assert.Equal(input.ExternalId, item.ExternalId); Assert.Equal(input.SourceUrl, item.SourceUrl);
        Assert.Equal("https://example.invalid/Job?B=2&a=1#F", item.NormalizedSourceUrl);
        Assert.Equal(ingested.Items[0].OpportunityId, item.OpportunityId); Assert.Equal(item.OpportunityId, item.OpportunityIdSnapshot);
        Assert.Equal(JsonValueKind.Object, item.PayloadSnapshot!.Value.ValueKind); Assert.Equal(JsonValueKind.Object, item.DecisionDetails!.Value.ValueKind);
        Assert.Equal(new[] { "external-id", "source-url" }, item.Sources.Select(x => x.RoleCode));
        Assert.All(item.Sources, x => Assert.Equal(x.OpportunitySourceIdSnapshot, x.OpportunitySourceId));
        Assert.Equal(setup.Configuration, item.SourceConfigurationId); Assert.Equal(setup.Search, item.SavedSearchId);
        Assert.Equal("manual", item.TriggerTypeCode); Assert.Equal("succeeded", item.ExecutionStatusCode);
        Assert.Equal(ingested.Execution.StartedAt, item.ExecutionStartedAt); Assert.Equal(setup.Pipeline, item.TargetPipelineId); Assert.Equal(setup.Stage, item.TargetPipelineStageId);
        Assert.Equal("ignored", page.Items[1].OutcomeCode); Assert.Empty(page.Items[1].Sources);
        await using var db = Db();
        var saved = await db.SourceExecutionItems.AsNoTracking().SingleAsync(x => x.Id == item.Id);
        SameJson(JsonSerializer.Deserialize<JsonElement>(saved.PayloadSnapshotJson!), item.PayloadSnapshot.Value);
        SameJson(JsonSerializer.Deserialize<JsonElement>(saved.DecisionDetailsJson!), item.DecisionDetails.Value);
    }

    [Fact]
    public async Task ExecutionPaginationAndExtensibleExactFiltersAreStable()
    {
        var setup = await PrepareAsync();
        var ingestion = (await IngestAsync(setup, Enumerable.Range(0, 7).Select(i => Item(i.ToString(), $"https://example.invalid/{i}")).ToArray())).Value!;
        using var factory = Factory(); using var client = Client(factory);
        var route = Execution(ingestion.Execution.Id) + "/items";
        var ids = new List<Guid>();
        for (var offset = 0; offset < 9; offset += 3)
        {
            var page = await Get<SourceExecutionItemsPageDto>(client, route + $"?offset={offset}&limit=3");
            Assert.Equal(7, page.TotalCount); Assert.Equal(offset + page.Items.Count < 7, page.HasMore);
            Assert.Equal(Enumerable.Range(offset, Math.Min(3, 7 - offset)), page.Items.Select(x => x.ItemIndex));
            ids.AddRange(page.Items.Select(x => x.Id));
        }
        Assert.Equal(7, ids.Distinct().Count());
        var beyond = await Get<SourceExecutionItemsPageDto>(client, route + "?offset=2147483647&limit=200");
        Assert.Equal(7, beyond.TotalCount); Assert.Empty(beyond.Items); Assert.False(beyond.HasMore);
        var matching = await Get<SourceExecutionItemsPageDto>(client, route + "?outcomeCode=created&decisionCode=CreatedNewOpportunity"); Assert.Equal(7, matching.TotalCount);
        foreach (var filter in new[] { "outcomeCode=pending", "decisionCode=creatednewopportunity", "decisionCode=FutureDecision", "decisionCode=%20CreatedNewOpportunity%20" })
            Assert.Equal(0, (await Get<SourceExecutionItemsPageDto>(client, route + "?" + filter)).TotalCount);
        await using var db = Db();
        await db.SourceExecutionItems.Where(x => x.Id == ids[0]).ExecuteUpdateAsync(s => s.SetProperty(x => x.DecisionCode, "FutureDecision"));
        Assert.Equal(1, (await Get<SourceExecutionItemsPageDto>(client, route + "?decisionCode=FutureDecision")).TotalCount);
    }

    [Fact]
    public async Task FailedBatchHistoryRemainsReadableWithOutcomeAndDecisionFilters()
    {
        var setup = await PrepareAsync();
        var failed = await IngestAsync(setup, Item("first"), Item(null, null, company: "No matching company"), Item("last", "https://example.invalid/last"));
        Assert.Equal(IngestionStatus.Conflict, failed.Status);
        using var factory = Factory(); using var client = Client(factory);
        var route = Execution(failed.Error!.ExecutionId!.Value) + "/items";
        var page = await Get<SourceExecutionItemsPageDto>(client, route);
        Assert.Equal(new[] { "rolled-back", "rejected", "not-processed" }, page.Items.Select(x => x.OutcomeCode));
        Assert.All(page.Items, x => { Assert.Null(x.OpportunityId); Assert.Null(x.OpportunityIdSnapshot); Assert.Empty(x.Sources); Assert.Equal("failed", x.ExecutionStatusCode); });
        Assert.Equal(JsonValueKind.Object, page.Items[0].DecisionDetails!.Value.ValueKind);
        Assert.Null(page.Items[1].DecisionDetails); Assert.Equal(JsonValueKind.Object, page.Items[2].DecisionDetails!.Value.ValueKind);
        foreach (var item in page.Items)
        {
            var filtered = await Get<SourceExecutionItemsPageDto>(client, route + $"?outcomeCode={item.OutcomeCode}&decisionCode={item.DecisionCode}&limit=1");
            Assert.Equal(1, filtered.TotalCount); Assert.Equal(item.Id, Assert.Single(filtered.Items).Id);
        }
    }

    private sealed class CancellationGate : SaveChangesInterceptor
    {
        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool used;
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!used && eventData.Context!.ChangeTracker.Entries<SourceExecutionItem>().Any(x => x.Entity.ItemIndex == 1 && x.Entity.OutcomeCode == "created"))
            {
                used = true; Reached.TrySetResult(); await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            return result;
        }
    }

    [Fact]
    public async Task CancelledBatchKeepsItsCountersAndTerminalItemsWhenRead()
    {
        var setup = await PrepareAsync(); var gate = new CancellationGate();
        await using var db = Db(gate); using var cancellation = new CancellationTokenSource(); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var task = new IngestionService(db, new FixedWorkspace(setup.Workspace), NullLogger<IngestionService>.Instance)
            .IngestAsync(setup.Search, new() { PipelineStageId = setup.Stage, Items = [Item("1"), Item("2", "https://example.invalid/2"), Item("3", "https://example.invalid/3")] }, cancellation.Token);
        try { await gate.Reached.Task.WaitAsync(timeout.Token); } finally { cancellation.Cancel(); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task.WaitAsync(timeout.Token));
        await using var verify = Db(); var id = await verify.SourceExecutions.Where(x => x.StatusCode == "cancelled").Select(x => x.Id).SingleAsync();
        using var factory = Factory(); using var client = Client(factory);
        var before = await Get<JsonElement>(client, Execution(id));
        var page = await Get<SourceExecutionItemsPageDto>(client, Execution(id) + "/items");
        Assert.Equal(new[] { "rolled-back", "cancelled", "not-processed" }, page.Items.Select(x => x.OutcomeCode));
        Assert.All(page.Items, x => { Assert.Null(x.OpportunityIdSnapshot); Assert.Equal("cancelled", x.ExecutionStatusCode); });
        Assert.Equal(JsonValueKind.Object, page.Items[1].DecisionDetails!.Value.ValueKind);
        SameJson(before, await Get<JsonElement>(client, Execution(id)));
    }

    [Fact]
    public async Task OpportunityObservationsUseParentFiltersDatesAndStableDescendingOrder()
    {
        var setup = await PrepareAsync();
        var first = (await IngestAsync(setup, Item())).Value!;
        await IngestAsync(setup, Item());
        await using var db = Db();
        var configuration = new SourceConfiguration { WorkspaceId = setup.Workspace, Name = "Other source", SourceTypeCode = "manual" };
        var search = new SavedSearch { WorkspaceId = setup.Workspace, SourceConfiguration = configuration, PipelineId = setup.Pipeline, Name = "Other search" };
        db.SavedSearches.Add(search); await db.SaveChangesAsync();
        await IngestAsync(setup with { Configuration = configuration.Id, Search = search.Id }, Item(null));
        var id = first.Items[0].OpportunityId;
        var received = new DateTimeOffset(2026, 1, 2, 10, 0, 0, TimeSpan.Zero);
        await db.SourceExecutionItems.ExecuteUpdateAsync(s => s.SetProperty(x => x.ReceivedAt, received));
        await db.Opportunities.Where(x => x.Id == id).ExecuteUpdateAsync(s => s.SetProperty(x => x.ArchivedAt, DateTimeOffset.UtcNow).SetProperty(x => x.Title, "Edited later"));
        var expected = await db.SourceExecutionItems.OrderByDescending(x => x.ReceivedAt).ThenByDescending(x => x.Id).Select(x => x.Id).ToArrayAsync();
        using var factory = Factory(); using var client = Client(factory);
        var page = await Get<OpportunityObservationsPageDto>(client, Opportunity(id));
        Assert.Equal(3, page.TotalCount); Assert.Equal(expected, page.Items.Select(x => x.Id));
        Assert.All(page.Items, x => Assert.Equal("Unity Developer", x.Title));
        var paged = new List<Guid>();
        for (var offset = 0; offset < 3; offset++)
        {
            var single = await Get<OpportunityObservationsPageDto>(client, Opportunity(id) + $"?offset={offset}&limit=1");
            Assert.Equal(3, single.TotalCount); Assert.Equal(offset < 2, single.HasMore); paged.Add(Assert.Single(single.Items).Id);
        }
        Assert.Equal(expected, paged);
        Assert.Equal(2, (await Get<OpportunityObservationsPageDto>(client, Opportunity(id) + $"?sourceConfigurationId={setup.Configuration}")).TotalCount);
        Assert.Equal(1, (await Get<OpportunityObservationsPageDto>(client, Opportunity(id) + $"?savedSearchId={search.Id}&outcomeCode=updated")).TotalCount);
        Assert.Equal(1, (await Get<OpportunityObservationsPageDto>(client, Opportunity(id) + "?decisionCode=CreatedNewOpportunity")).TotalCount);
        Assert.Equal(3, (await Get<OpportunityObservationsPageDto>(client, Opportunity(id) + "?from=2026-01-02T12:00:00%2B02:00&to=2026-01-02T12:00:00%2B02:00")).TotalCount);
        Assert.Equal(0, (await Get<OpportunityObservationsPageDto>(client, Opportunity(id) + "?from=2026-01-02T10:00:01Z")).TotalCount);
        Assert.Equal(0, (await Get<OpportunityObservationsPageDto>(client, Opportunity(id) + "?to=2026-01-02T09:59:59Z")).TotalCount);
    }

    [Fact]
    public async Task SourceObservationsCountDistinctItemsAndReturnAllRolesEvenWhenFiltered()
    {
        var setup = await PrepareAsync(); var first = (await IngestAsync(setup, Item())).Value!;
        await using var db = Db(); var source = await db.OpportunitySources.AsNoTracking().SingleAsync();
        using var factory = Factory(); using var client = Client(factory);
        var route = Source(source.OpportunityId, source.Id);
        foreach (var filter in new[] { "", "?roleCode=external-id", "?roleCode=source-url" })
        {
            var page = await Get<OpportunitySourceObservationsPageDto>(client, route + filter);
            Assert.Equal(1, page.TotalCount); Assert.Equal(2, Assert.Single(page.Items).Sources.Count); Assert.False(page.HasMore);
            Assert.Equal(source.Id, page.OpportunitySourceId); Assert.Equal(source.OpportunityId, page.OpportunityId);
        }
        var second = (await IngestAsync(setup, Item("one", "https://example.invalid/alias"))).Value!;
        var filtered = await Get<OpportunitySourceObservationsPageDto>(client, route + "?roleCode=external-id&limit=1&outcomeCode=updated");
        Assert.Equal(1, filtered.TotalCount); var item = Assert.Single(filtered.Items);
        Assert.Equal(second.Execution.Id, item.SourceExecutionId); Assert.Equal(2, item.Sources.Count);
        Assert.Equal(2, item.Sources.Select(x => x.OpportunitySourceId).Distinct().Count());
        Assert.Equal(new[] { "external-id", "source-url" }, item.Sources.Select(x => x.RoleCode));
        var page0 = await Get<OpportunitySourceObservationsPageDto>(client, route + "?limit=1");
        var page1 = await Get<OpportunitySourceObservationsPageDto>(client, route + "?limit=1&offset=1");
        Assert.Equal(2, page0.TotalCount); Assert.True(page0.HasMore); Assert.False(page1.HasMore);
        Assert.NotEqual(page0.Items[0].Id, page1.Items[0].Id);
        Assert.Equal(1, (await Get<OpportunitySourceObservationsPageDto>(client, route + "?roleCode=source-url")).TotalCount);
        Assert.Equal(0, (await Get<OpportunitySourceObservationsPageDto>(client, route + "?from=2100-01-01T00:00:00Z")).TotalCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PhysicalDeletionPreservesExecutionSnapshotsButRemovesLiveRoutes(bool deleteOpportunity)
    {
        var setup = await PrepareAsync(); var result = (await IngestAsync(setup, Item())).Value!;
        await using var db = Db(); var source = await db.OpportunitySources.AsNoTracking().SingleAsync();
        using var factory = Factory(); using var client = Client(factory);
        if (deleteOpportunity) await db.Opportunities.Where(x => x.Id == source.OpportunityId).ExecuteDeleteAsync();
        else await db.OpportunitySources.Where(x => x.Id == source.Id).ExecuteDeleteAsync(); // Controlled SQL tests SET NULL, not the protected CRUD.
        var item = Assert.Single((await Get<SourceExecutionItemsPageDto>(client, Execution(result.Execution.Id) + "/items")).Items);
        Assert.Equal(source.OpportunityId, item.OpportunityIdSnapshot);
        Assert.Equal(deleteOpportunity ? (Guid?)null : source.OpportunityId, item.OpportunityId);
        Assert.All(item.Sources, link => { Assert.Null(link.OpportunitySourceId); Assert.Equal(source.Id, link.OpportunitySourceIdSnapshot); });
        using var missingSource = await client.GetAsync(Source(source.OpportunityId, source.Id)); Assert.Equal(HttpStatusCode.NotFound, missingSource.StatusCode);
        using var opportunity = await client.GetAsync(Opportunity(source.OpportunityId));
        Assert.Equal(deleteOpportunity ? HttpStatusCode.NotFound : HttpStatusCode.OK, opportunity.StatusCode);
        if (!deleteOpportunity)
        {
            using var manual = await client.PostAsJsonAsync($"/api/opportunities/{source.OpportunityId}/sources", new
                { sourceLabel = "Unused", sourceUrl = "https://example.invalid/unused" });
            Assert.Equal(HttpStatusCode.Created, manual.StatusCode);
            var manualId = (await manual.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
            Assert.Equal(0, (await Get<OpportunitySourceObservationsPageDto>(client, Source(source.OpportunityId, manualId))).TotalCount);
            using var delete = await client.DeleteAsync($"/api/opportunities/{source.OpportunityId}/sources/{manualId}"); Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
            using var missing = await client.GetAsync(Source(source.OpportunityId, manualId)); Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        }
    }

    [Fact]
    public async Task InvalidQueriesHaveControlledProblemCodesOnEveryApplicableRoute()
    {
        var setup = await PrepareAsync(); var ingested = (await IngestAsync(setup, Item())).Value!;
        await using var db = Db(); var source = await db.OpportunitySources.AsNoTracking().SingleAsync();
        using var factory = Factory(); using var client = Client(factory);
        var routes = new[] { Execution(ingested.Execution.Id) + "/items", Opportunity(source.OpportunityId), Source(source.OpportunityId, source.Id) };
        var cases = new[]
        {
            ("offset=-1", "InvalidPagination"), ("limit=0", "InvalidPagination"), ("limit=201", "InvalidPagination"),
            ("offset=bad", "InvalidPagination"), ("limit=2147483648", "InvalidPagination"),
            ("outcomeCode=unknown", "InvalidOutcomeCode"), ("outcomeCode=Created", "InvalidOutcomeCode"),
            ("decisionCode=%20%20", "InvalidDecisionCode"), ("decisionCode=", "InvalidDecisionCode"),
            ("decisionCode=" + new string('x', 101), "InvalidDecisionCode")
        };
        async Task Check(string route, string code)
        {
            using var response = await client.GetAsync(route);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
            var body = await response.Content.ReadAsStringAsync();
            var problem = JsonSerializer.Deserialize<JsonElement>(body);
            Assert.Equal(code, problem.GetProperty("code").GetString()); Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("detail").GetString()));
            Assert.DoesNotContain("SELECT", body); Assert.DoesNotContain("Npgsql", body); Assert.DoesNotContain("Exception", body);
        }
        foreach (var route in routes) foreach (var (query, code) in cases) await Check(route + "?" + query, code);
        foreach (var route in routes.Skip(1))
        {
            await Check(route + "?from=2026-02-01T00:00:00Z&to=2026-01-01T00:00:00Z", "InvalidDateRange");
            await Check(route + "?from=bad", "InvalidDateRange");
        }
        await Check(routes[2] + "?roleCode=title-company", "InvalidRoleCode");
        await Check(routes[1] + "?savedSearchId=bad", "InvalidReference");
    }

    [Fact]
    public async Task WorkspaceAndNestedOwnershipIsolationDoNotExposeForeignSnapshots()
    {
        var setup = await PrepareAsync(); var own = (await IngestAsync(setup, Item())).Value!;
        await using var db = Db(); var foreign = await SeedAsync(db);
        var other = (await IngestAsync(foreign, Item(title: "Foreign confidential title"))).Value!;
        await db.Workspaces.Where(x => x.Id == foreign.Workspace).ExecuteUpdateAsync(s => s.SetProperty(x => x.ArchivedAt, DateTimeOffset.UtcNow));
        var foreignSource = await db.OpportunitySources.AsNoTracking().SingleAsync(x => x.WorkspaceId == foreign.Workspace);
        var ownSource = await db.OpportunitySources.AsNoTracking().SingleAsync(x => x.WorkspaceId == setup.Workspace);
        using var factory = Factory(); using var client = Client(factory);
        foreach (var route in new[] { Execution(other.Execution.Id) + "/history", Execution(other.Execution.Id) + "/items",
            Opportunity(foreignSource.OpportunityId), Source(foreignSource.OpportunityId, foreignSource.Id),
            Source(ownSource.OpportunityId, foreignSource.Id), Source(setup.Opportunity, ownSource.Id),
            Execution(Guid.NewGuid()) + "/history", Execution(Guid.NewGuid()) + "/items", Opportunity(Guid.NewGuid()) })
        {
            using var response = await client.GetAsync(route); Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.DoesNotContain("Foreign confidential", await response.Content.ReadAsStringAsync());
        }
        foreach (var query in new[] { $"sourceConfigurationId={foreign.Configuration}", $"savedSearchId={foreign.Search}", $"sourceConfigurationId={Guid.NewGuid()}" })
        {
            var page = await Get<OpportunityObservationsPageDto>(client, Opportunity(ownSource.OpportunityId) + "?" + query);
            Assert.Equal(0, page.TotalCount); Assert.Empty(page.Items);
        }
        Assert.Equal(1, (await Get<SourceExecutionItemsPageDto>(client, Execution(own.Execution.Id) + "/items")).TotalCount);
    }

    private async Task<string> DatabaseSnapshot()
    {
        await using var connection = new NpgsqlConnection(Postgres.GetConnectionString()); await connection.OpenAsync();
        var tables = new List<string>();
        await using (var names = new NpgsqlCommand("SELECT tablename FROM pg_tables WHERE schemaname = 'public' ORDER BY tablename", connection))
        await using (var reader = await names.ExecuteReaderAsync())
            while (await reader.ReadAsync()) tables.Add(reader.GetString(0));
        var snapshots = new List<string>();
        using var identifiers = new NpgsqlCommandBuilder();
        foreach (var table in tables)
        {
            var sql = "SELECT COALESCE(jsonb_agg(to_jsonb(t) ORDER BY to_jsonb(t)::text), '[]'::jsonb)::text FROM " + identifiers.QuoteIdentifier(table) + " t";
            await using var rows = new NpgsqlCommand(sql, connection);
            snapshots.Add(table + ":" + (string)(await rows.ExecuteScalarAsync())!);
        }
        return string.Join("\n", snapshots);
    }

    [Fact]
    public async Task AllReadRoutesLeaveEveryDatabaseRowAndTimestampUnchanged()
    {
        var setup = await PrepareAsync(); var result = (await IngestAsync(setup, Item())).Value!;
        await using var db = Db(); var source = await db.OpportunitySources.AsNoTracking().SingleAsync();
        using var factory = Factory(); using var client = Client(factory);
        var before = await DatabaseSnapshot();
        foreach (var route in new[] { Execution(result.Execution.Id) + "/history", Execution(result.Execution.Id) + "/items",
            Opportunity(source.OpportunityId), Source(source.OpportunityId, source.Id), Execution(setup.Execution) + "/history", Execution(setup.Execution) + "/items" })
        {
            await Get<JsonElement>(client, route);
            foreach (var method in new[] { HttpMethod.Post, HttpMethod.Put, HttpMethod.Delete })
            {
                using var request = new HttpRequestMessage(method, route); using var response = await client.SendAsync(request);
                Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
            }
        }
        Assert.Equal(before, await DatabaseSnapshot());
    }

    private sealed class QueryRecorder : DbCommandInterceptor
    {
        public List<string> Commands { get; } = [];
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText); return ValueTask.FromResult(result);
        }
    }

    [Fact]
    public async Task PagesUseBoundedSqlQueriesAndJsonSurvivesContextDisposal()
    {
        var setup = await PrepareAsync();
        var result = (await IngestAsync(setup, Enumerable.Range(0, 8).Select(i => Item(i.ToString(), $"https://example.invalid/{i}")).ToArray())).Value!;
        var recorder = new QueryRecorder(); SourceExecutionItemsPageDto page;
        await using (var db = Db(recorder))
        {
            var service = new IngestionHistoryReadService(db, new FixedWorkspace(setup.Workspace));
            page = (await service.GetExecutionItemsAsync(result.Execution.Id, new() { Offset = 2, Limit = 3 }, default)).Value!;
            Assert.Empty(db.ChangeTracker.Entries());
        }
        Assert.Equal(8, page.TotalCount); Assert.Equal(3, page.Items.Count); Assert.Equal(4, recorder.Commands.Count);
        Assert.Contains("count(*)", recorder.Commands[1]); Assert.Contains("LIMIT", recorder.Commands[2]); Assert.Contains("OFFSET", recorder.Commands[2]);
        Assert.Contains("ANY", recorder.Commands[3]); Assert.DoesNotContain("ConfigurationJson", string.Join("\n", recorder.Commands));
        Assert.All(page.Items, x => { Assert.Null(x.PayloadSnapshot); Assert.Equal(JsonValueKind.Object, x.DecisionDetails!.Value.ValueKind); });
        recorder.Commands.Clear();
        var sourceId = page.Items[0].Sources[0].OpportunitySourceId!.Value;
        await using var sourceDb = Db(recorder);
        var sourcePage = (await new IngestionHistoryReadService(sourceDb, new FixedWorkspace(setup.Workspace))
            .GetSourceObservationsAsync(page.Items[0].OpportunityId!.Value, sourceId, new() { Limit = 1 }, default)).Value!;
        Assert.Equal(1, sourcePage.TotalCount); Assert.Equal(2, Assert.Single(sourcePage.Items).Sources.Count);
        Assert.Equal(4, recorder.Commands.Count); Assert.Contains("EXISTS", recorder.Commands[1]); Assert.Contains("LIMIT", recorder.Commands[2]);
    }
}
