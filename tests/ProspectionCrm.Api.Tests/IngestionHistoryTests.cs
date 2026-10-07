using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
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
using ProspectionCrm.Api.Data;
using ProspectionCrm.Api.Dtos.Ingestions;
using ProspectionCrm.Api.Dtos.SourceExecutions;
using ProspectionCrm.Api.Entities;
using ProspectionCrm.Api.Services;
using Testcontainers.PostgreSql;
using Xunit;
using static ProspectionCrm.Api.Services.IngestionHistoryCodes;

namespace ProspectionCrm.Api.Tests;

public sealed class IngestionHistoryTests : IAsyncLifetime
{
    private const string PreviousMigration = "20260929150147_Phase42PipelineLifecycleAndDefault";
    private readonly PostgreSqlContainer postgres = new PostgreSqlBuilder("postgres:18").Build();
    public Task InitializeAsync() => postgres.StartAsync();
    public Task DisposeAsync() => postgres.DisposeAsync().AsTask();
    private ProspectionCrmDbContext Db(params IInterceptor[] interceptors) => new(
        new DbContextOptionsBuilder<ProspectionCrmDbContext>().UseNpgsql(postgres.GetConnectionString())
            .AddInterceptors(interceptors).Options);
    private sealed record Setup(Guid WorkspaceId, Guid SearchId, Guid SourceId, Guid PipelineId, Guid StageId);
    private async Task<Setup> PrepareAsync()
    {
        await using var db = Db();
        await db.Database.MigrateAsync();
        var workspace = new Workspace { Name = "History", TimeZoneId = "UTC",
            OwnerUser = new UserAccount { Email = "history@example.invalid" } };
        var pipeline = new Pipeline { Workspace = workspace, Name = "Pipeline before", TypeCode = "custom" };
        var stage = new PipelineStage { Pipeline = pipeline, Name = "Stage before", CategoryCode = "active", SortOrder = 0 };
        var source = new SourceConfiguration { Workspace = workspace, Name = "Source before", SourceTypeCode = "manual",
            BaseUrl = "https://example.invalid", ConfigurationJson = """{"token":"never-copy-secret","option":1}""" };
        var search = new SavedSearch { Workspace = workspace, Pipeline = pipeline, SourceConfiguration = source,
            Name = "Search before", SearchUrl = "https://example.invalid/search", CriteriaJson = """{"keywords":["Unity"],"remote":true}""" };
        db.PipelineStages.Add(stage);
        db.SavedSearches.Add(search);
        await db.SaveChangesAsync();
        return new(workspace.Id, search.Id, source.Id, pipeline.Id, stage.Id);
    }
    private static IngestionItemRequest Item(string? external = "one", string? url = "https://example.invalid/Job",
        string title = " Engineer ", string? company = null) => new()
    {
        Title = title, CompanyName = company, ExternalId = external, SourceUrl = url,
        Location = " Bordeaux ", Description = " Description "
    };
    private async Task<IngestionResult> IngestAsync(Setup setup, params IngestionItemRequest[] items)
    {
        await using var db = Db();
        return await new IngestionService(db, new CurrentWorkspaceProvider(db), NullLogger<IngestionService>.Instance)
            .IngestAsync(setup.SearchId, new() { PipelineStageId = setup.StageId, Items = items.ToList() }, default);
    }
    private async Task<SourceExecution> HistoryAsync(Guid id)
    {
        await using var db = Db();
        var execution = await db.SourceExecutions.AsNoTracking().Include(x => x.Items).ThenInclude(x => x.Sources)
            .SingleAsync(x => x.Id == id);
        Assert.Equal(1, execution.HistoryVersion);
        Assert.Equal(execution.ItemsFound, execution.Items.Count);
        Assert.Equal(execution.ItemsFound, execution.ItemsCreated + execution.ItemsUpdated + execution.ItemsIgnored
            + execution.ItemsRejected + execution.ItemsRolledBack + execution.ItemsNotProcessed + execution.ItemsCancelled);
        Assert.All(execution.Items, item =>
        {
            Assert.NotEqual(Outcomes.Pending, item.OutcomeCode);
            Assert.True(item.ProcessedAt >= item.ReceivedAt);
            Assert.Equal(execution.StartedAt, item.ReceivedAt);
            Assert.Equal(execution.WorkspaceId, item.WorkspaceId);
        });
        return execution;
    }

    [Fact]
    public async Task SuccessKeepsEveryRawInputNormalizedKeyPayloadAndSourceRole()
    {
        var setup = await PrepareAsync();
        var input = Item(" External ", " HTTPS://EXAMPLE.INVALID/Job?B=2&a=1#F ", "  Unity\tEngineer ", " Crew  Rats ");
        var result = await IngestAsync(setup, input);
        Assert.Equal(IngestionStatus.Succeeded, result.Status);
        var execution = await HistoryAsync(result.Value!.Execution.Id);
        Assert.Equal((1, 1, 1), (execution.HistoryVersion, execution.ContractVersion, execution.NormalizationVersion));
        Assert.Equal((setup.PipelineId, setup.StageId), (execution.TargetPipelineId, execution.TargetPipelineStageId));
        Assert.Equal((1, 0, 0, 0, 0, 0, 0), (execution.ItemsCreated, execution.ItemsUpdated, execution.ItemsIgnored,
            execution.ItemsRejected, execution.ItemsRolledBack, execution.ItemsNotProcessed, execution.ItemsCancelled));
        var item = Assert.Single(execution.Items);
        Assert.Equal(0, item.ItemIndex);
        Assert.Equal((input.Title, "UNITY ENGINEER"), (item.Title, item.NormalizedTitle));
        Assert.Equal((input.CompanyName, "CREW RATS"), (item.CompanyName, item.NormalizedCompanyName));
        Assert.Equal(input.ExternalId, item.ExternalId);
        Assert.Equal(input.SourceUrl, item.SourceUrl);
        Assert.Equal("https://example.invalid/Job?B=2&a=1#F", item.NormalizedSourceUrl);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(item.PayloadSnapshotJson!),
            JsonSerializer.SerializeToNode(new { location = input.Location, description = input.Description })));
        Assert.Equal((Outcomes.Created, CreatedNewOpportunity), (item.OutcomeCode, item.DecisionCode));
        Assert.Equal(result.Value.Items[0].OpportunityId, item.OpportunityId);
        Assert.Equal(item.OpportunityId, item.OpportunityIdSnapshot);
        Assert.Equal(new[] { Identities.ExternalId, Identities.SourceUrl },
            JsonNode.Parse(item.DecisionDetailsJson!)!["providedIdentities"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.Equal(new[] { Identities.ExternalId, Identities.SourceUrl }, item.Sources.Select(x => x.RoleCode).Order());
        Assert.Single(item.Sources.Select(x => x.OpportunitySourceIdSnapshot).Distinct());
        Assert.All(item.Sources, x => Assert.Equal(x.OpportunitySourceIdSnapshot, x.OpportunitySourceId));
    }

    [Theory]
    [InlineData("external", MatchedExternalId)]
    [InlineData("url", MatchedSourceUrl)]
    [InlineData("both", MatchedConsistentIdentities)]
    public async Task ReplayKeepsSeparateImmutableHistoryAndExactMatchDecision(string identity, string decision)
    {
        var setup = await PrepareAsync();
        var input = identity == "external" ? Item(url: null) : identity == "url" ? Item(external: null) : Item();
        var first = (await IngestAsync(setup, input)).Value!;
        var previous = await HistoryAsync(first.Execution.Id);
        var second = (await IngestAsync(setup, input)).Value!;
        Assert.NotEqual(first.Execution.Id, second.Execution.Id);
        var history = await HistoryAsync(second.Execution.Id);
        Assert.Equal((0, 1, 0), (history.ItemsCreated, history.ItemsUpdated, history.ItemsIgnored));
        var item = Assert.Single(history.Items);
        Assert.Equal((Outcomes.Updated, decision), (item.OutcomeCode, item.DecisionCode));
        Assert.Equal(first.Items[0].OpportunityId, item.OpportunityIdSnapshot);
        Assert.NotEqual(previous.Items.Single().Id, item.Id);
        Assert.Equal(previous.Items.Single().DecisionDetailsJson, (await HistoryAsync(first.Execution.Id)).Items.Single().DecisionDetailsJson);
    }

    [Fact]
    public async Task TitleCompanyFallbackAndInternalDuplicateKeepDistinctDecisions()
    {
        var setup = await PrepareAsync();
        await using var db = Db();
        var opportunity = new Opportunity { WorkspaceId = setup.WorkspaceId, PipelineStageId = setup.StageId,
            Title = "Unity Engineer", PriorityCode = "high",
            Company = new Company { WorkspaceId = setup.WorkspaceId, Name = "Crew Rats" } };
        db.Opportunities.Add(opportunity);
        await db.SaveChangesAsync();
        var first = Item(null, null, " Unity  Engineer ", " Crew\tRats ");
        var result = (await IngestAsync(setup, first, Item(null, null, "UNITY ENGINEER", "CREW RATS"))).Value!;
        var history = await HistoryAsync(result.Execution.Id);
        var items = history.Items.OrderBy(x => x.ItemIndex).ToArray();
        Assert.Equal((0, 1, 1), (history.ItemsCreated, history.ItemsUpdated, history.ItemsIgnored));
        Assert.Equal((Outcomes.Updated, MatchedTitleCompany), (items[0].OutcomeCode, items[0].DecisionCode));
        Assert.Equal(first.CompanyName, items[0].CompanyName);
        Assert.Equal((Outcomes.Ignored, DuplicateInBatch), (items[1].OutcomeCode, items[1].DecisionCode));
        Assert.Equal(0, JsonNode.Parse(items[1].DecisionDetailsJson!)!["duplicateOfItemIndex"]!.GetValue<int>());
        Assert.All(items, x => { Assert.Equal(opportunity.Id, x.OpportunityIdSnapshot); Assert.Empty(x.Sources); });
        Assert.Empty(await db.OpportunitySources.ToArrayAsync()); // Fallback is carried by history, not an empty provenance.
    }

    [Fact]
    public async Task AliasAndCrossConfigurationSourcesAreLinkedWithTheirActualRoles()
    {
        var setup = await PrepareAsync();
        var first = (await IngestAsync(setup, Item())).Value!;
        var alias = (await IngestAsync(setup, Item(url: "https://example.invalid/Other"))).Value!;
        var aliasItem = Assert.Single((await HistoryAsync(alias.Execution.Id)).Items);
        Assert.Equal(2, aliasItem.Sources.Select(x => x.OpportunitySourceIdSnapshot).Distinct().Count());
        await using var db = Db();
        var other = new SourceConfiguration { WorkspaceId = setup.WorkspaceId, Name = "Other", SourceTypeCode = "manual" };
        db.SourceConfigurations.Add(other);
        var search = await db.SavedSearches.SingleAsync();
        search.SourceConfiguration = other;
        await db.SaveChangesAsync();
        var replay = (await IngestAsync(setup, Item("different"))).Value!;
        var item = Assert.Single((await HistoryAsync(replay.Execution.Id)).Items);
        Assert.Equal(first.Items[0].OpportunityId, item.OpportunityId);
        Assert.Equal(MatchedSourceUrl, item.DecisionCode);
        Assert.Equal(2, item.Sources.Count);
        var externalSourceId = item.Sources.Single(x => x.RoleCode == Identities.ExternalId).OpportunitySourceId;
        var urlSourceId = item.Sources.Single(x => x.RoleCode == Identities.SourceUrl).OpportunitySourceId;
        var ext = await db.OpportunitySources.SingleAsync(x => x.Id == externalSourceId);
        Assert.Equal(other.Id, ext.SourceConfigurationId);
        Assert.Null(ext.SourceUrl);
        Assert.Equal(setup.SourceId, (await db.OpportunitySources.SingleAsync(x => x.Id == urlSourceId)).SourceConfigurationId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ContextHasExactShapeHashAndRemainsUnchangedAfterResourceEdits(bool nullConfiguration)
    {
        var setup = await PrepareAsync();
        await using var db = Db();
        if (nullConfiguration)
            await db.SourceConfigurations.ExecuteUpdateAsync(s => s.SetProperty(x => x.ConfigurationJson, (string?)null));
        var source = await db.SourceConfigurations.SingleAsync();
        var result = (await IngestAsync(setup, Item())).Value!;
        var history = await HistoryAsync(result.Execution.Id);
        var expected = JsonSerializer.SerializeToNode(new
        {
            schemaVersion = 1,
            sourceConfiguration = new { id = setup.SourceId, name = "Source before", sourceTypeCode = "manual",
                baseUrl = "https://example.invalid", configurationSha256 = source.ConfigurationJson is null ? null
                    : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(source.ConfigurationJson))) },
            savedSearch = new { id = setup.SearchId, name = "Search before", searchUrl = "https://example.invalid/search",
                criteria = new { keywords = new[] { "Unity" }, remote = true } },
            pipeline = new { id = setup.PipelineId, name = "Pipeline before", typeCode = "custom" },
            targetStage = new { id = setup.StageId, name = "Stage before", categoryCode = "active" }
        });
        Assert.True(JsonNode.DeepEquals(expected, JsonNode.Parse(history.ContextSnapshotJson!)));
        Assert.DoesNotContain("never-copy-secret", history.ContextSnapshotJson);
        Assert.DoesNotContain("configurationJson", history.ContextSnapshotJson);
        source.Name = "Source after"; source.ConfigurationJson = """{"changed":true}""";
        var search = await db.SavedSearches.SingleAsync();
        search.Name = "Search after"; search.CriteriaJson = "{}";
        (await db.Pipelines.SingleAsync()).Name = "Pipeline after";
        (await db.PipelineStages.SingleAsync()).Name = "Stage after";
        await db.SaveChangesAsync();
        Assert.Equal(history.ContextSnapshotJson, (await HistoryAsync(result.Execution.Id)).ContextSnapshotJson);
        Assert.DoesNotContain("contextSnapshotJson", JsonSerializer.Serialize(result.Execution, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MidBatchFailureKeepsInputsButRollsBackAllBusinessDecisions(bool technical)
    {
        var setup = await PrepareAsync();
        await using var db = Db();
        if (technical)
            await db.Database.ExecuteSqlRawAsync("""
                ALTER TABLE "OpportunitySources" ADD CONSTRAINT reject_history_source CHECK ("ExternalId" <> 'reject')
                """);
        else
        {
            await IngestAsync(setup, Item("a", "https://example.invalid/a"));
            await IngestAsync(setup, Item("b", "https://example.invalid/b"));
        }
        var beforeOpportunities = await db.Opportunities.CountAsync();
        var beforeSources = await db.OpportunitySources.CountAsync();
        var conflict = technical ? Item("reject") : Item("a", "https://example.invalid/b");
        var result = await IngestAsync(setup, Item("new", "https://example.invalid/new"), conflict,
            Item("unprocessed", "https://example.invalid/unprocessed"));
        Assert.Equal(technical ? IngestionStatus.Failed : IngestionStatus.Conflict, result.Status);
        var history = await HistoryAsync(result.Error!.ExecutionId!.Value);
        Assert.Equal("failed", history.StatusCode);
        Assert.Equal((0, 0, 0, 1, 1, 1, 0), (history.ItemsCreated, history.ItemsUpdated, history.ItemsIgnored,
            history.ItemsRejected, history.ItemsRolledBack, history.ItemsNotProcessed, history.ItemsCancelled));
        var items = history.Items.OrderBy(x => x.ItemIndex).ToArray();
        Assert.Equal(new[] { Outcomes.RolledBack, Outcomes.Rejected, Outcomes.NotProcessed }, items.Select(x => x.OutcomeCode));
        Assert.Equal(new[] { RolledBackAfterFailure, technical ? PersistenceFailure : AmbiguousIdentity, NotProcessedAfterFailure },
            items.Select(x => x.DecisionCode));
        var provisional = JsonNode.Parse(items[0].DecisionDetailsJson!)!;
        Assert.Equal(Outcomes.Created, provisional["provisionalOutcomeCode"]!.GetValue<string>());
        Assert.NotNull(provisional["provisionalOpportunityId"]);
        Assert.Equal(1, JsonNode.Parse(items[2].DecisionDetailsJson!)!["blockedByItemIndex"]!.GetValue<int>());
        if (!technical)
            Assert.Equal((await db.Opportunities.Select(x => x.Id).ToArrayAsync()).Order(),
                JsonNode.Parse(items[1].DecisionDetailsJson!)!["candidateOpportunityIds"]!.AsArray().Select(x => x!.GetValue<Guid>()).Order());
        else Assert.Null(items[1].DecisionDetailsJson);
        Assert.All(items, x => { Assert.Null(x.OpportunityId); Assert.Null(x.OpportunityIdSnapshot); Assert.Empty(x.Sources); });
        Assert.Equal(beforeOpportunities, await db.Opportunities.CountAsync());
        Assert.Equal(beforeSources, await db.OpportunitySources.CountAsync());
        Assert.DoesNotContain("reject_history_source", JsonSerializer.Serialize(result));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PhysicalDeletionNullsLiveLinksButPreservesSnapshots(bool deleteOpportunity)
    {
        var setup = await PrepareAsync();
        var result = (await IngestAsync(setup, Item())).Value!;
        var original = Assert.Single((await HistoryAsync(result.Execution.Id)).Items);
        await using var db = Db();
        if (deleteOpportunity) await db.Opportunities.ExecuteDeleteAsync();
        else await db.OpportunitySources.ExecuteDeleteAsync();
        var item = Assert.Single((await HistoryAsync(result.Execution.Id)).Items);
        Assert.Equal(original.OpportunityIdSnapshot, item.OpportunityIdSnapshot);
        if (deleteOpportunity) Assert.Null(item.OpportunityId);
        else Assert.Equal(original.OpportunityId, item.OpportunityId);
        Assert.Equal(original.Sources.Select(x => x.OpportunitySourceIdSnapshot).Order(), item.Sources.Select(x => x.OpportunitySourceIdSnapshot).Order());
        Assert.All(item.Sources, x => Assert.Null(x.OpportunitySourceId));
        await db.SourceExecutions.ExecuteDeleteAsync();
        Assert.Empty(await db.SourceExecutionItems.ToArrayAsync());
        Assert.Empty(await db.SourceExecutionItemSources.ToArrayAsync());
    }

    private sealed class CancellationGate(bool finalization) : SaveChangesInterceptor
    {
        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool used;
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            var db = eventData.Context!;
            var shouldCancel = finalization
                ? db.ChangeTracker.Entries<SourceExecution>().Any(x => x.Entity.StatusCode == "succeeded")
                : db.ChangeTracker.Entries<SourceExecutionItem>().Any(x => x.Entity.ItemIndex == 1 && x.Entity.OutcomeCode == Outcomes.Created);
            if (!used && shouldCancel)
            {
                used = true;
                Reached.TrySetResult();
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            return result;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationGatePersistsTerminalHistoryWithIndependentCleanupToken(bool finalization)
    {
        var setup = await PrepareAsync();
        var gate = new CancellationGate(finalization);
        await using var db = Db(gate);
        using var cancellation = new CancellationTokenSource();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var service = new IngestionService(db, new CurrentWorkspaceProvider(db), NullLogger<IngestionService>.Instance);
        var task = service.IngestAsync(setup.SearchId, new() { PipelineStageId = setup.StageId, Items =
            [Item("1", "https://example.invalid/1"), Item("2", "https://example.invalid/2"), Item("3", "https://example.invalid/3")] }, cancellation.Token);
        try { await gate.Reached.Task.WaitAsync(timeout.Token); }
        finally { cancellation.Cancel(); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await task.WaitAsync(timeout.Token));
        await using var verify = Db();
        var history = await HistoryAsync(await verify.SourceExecutions.Select(x => x.Id).SingleAsync());
        Assert.Equal(("cancelled", RequestCancelled), (history.StatusCode, history.ErrorMessage));
        Assert.Equal(finalization ? (0, 3, 0, 0) : (0, 1, 1, 1),
            (history.ItemsRejected, history.ItemsRolledBack, history.ItemsNotProcessed, history.ItemsCancelled));
        var items = history.Items.OrderBy(x => x.ItemIndex).ToArray();
        Assert.Equal(finalization ? new[] { Outcomes.RolledBack, Outcomes.RolledBack, Outcomes.RolledBack }
            : new[] { Outcomes.RolledBack, Outcomes.Cancelled, Outcomes.NotProcessed }, items.Select(x => x.OutcomeCode));
        if (!finalization)
            Assert.Equal(1, JsonNode.Parse(items[1].DecisionDetailsJson!)!["cancelledAtItemIndex"]!.GetValue<int>());
        Assert.All(items, x => { Assert.Null(x.OpportunityId); Assert.Null(x.OpportunityIdSnapshot); Assert.Empty(x.Sources); });
        Assert.Empty(await verify.Opportunities.ToArrayAsync());
        Assert.Empty(await verify.OpportunitySources.ToArrayAsync());
    }

    [Fact]
    public async Task UpgradePreservesRealLegacyRowsWithoutFabricatingHistoryAndDownIsCoherent()
    {
        await using var db = Db();
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync(PreviousMigration);
        var user = Guid.NewGuid(); var workspace = Guid.NewGuid(); var source = Guid.NewGuid(); var execution = Guid.NewGuid();
        // Only SQL compatible with the old schema. Never SaveChanges with today's model.
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "UserAccounts" ("Id", "Email", "CreatedAt") VALUES ({user}, 'legacy@example.invalid', now());
            INSERT INTO "Workspaces" ("Id", "OwnerUserId", "Name", "TimeZoneId", "CreatedAt")
                VALUES ({workspace}, {user}, 'Legacy', 'UTC', now());
            INSERT INTO "SourceConfigurations" ("Id", "WorkspaceId", "Name", "SourceTypeCode", "Enabled", "CreatedAt")
                VALUES ({source}, {workspace}, 'Legacy source', 'manual', true, now());
            INSERT INTO "SourceExecutions" ("Id", "WorkspaceId", "SourceConfigurationId", "TriggerTypeCode",
                "StatusCode", "StartedAt", "FinishedAt", "ItemsFound", "ItemsCreated", "ItemsUpdated", "ItemsIgnored", "ErrorMessage")
                VALUES ({execution}, {workspace}, {source}, 'manual', 'failed', now(), now(), 3, 0, 0, 3, 'AmbiguousIdentity');
            """);
        await using var connection = new NpgsqlConnection(postgres.GetConnectionString());
        await connection.OpenAsync();
        async Task<string> LegacyJsonAsync()
        {
            await using var command = new NpgsqlCommand("""
                SELECT jsonb_build_object('id', "Id", 'workspace', "WorkspaceId", 'source', "SourceConfigurationId",
                    'trigger', "TriggerTypeCode", 'status', "StatusCode", 'start', "StartedAt", 'finish', "FinishedAt",
                    'found', "ItemsFound", 'created', "ItemsCreated", 'updated', "ItemsUpdated", 'ignored', "ItemsIgnored",
                    'error', "ErrorMessage")::text FROM "SourceExecutions"
                """, connection);
            return (string)(await command.ExecuteScalarAsync())!;
        }
        var before = await LegacyJsonAsync();
        await db.Database.MigrateAsync();
        Assert.Equal(before, await LegacyJsonAsync());
        var legacy = await db.SourceExecutions.AsNoTracking().SingleAsync();
        Assert.Null(legacy.HistoryVersion); Assert.Null(legacy.ContractVersion); Assert.Null(legacy.NormalizationVersion);
        Assert.Null(legacy.TargetPipelineId); Assert.Null(legacy.TargetPipelineStageId); Assert.Null(legacy.ContextSnapshotJson);
        Assert.Equal((0, 0, 0, 0), (legacy.ItemsRejected, legacy.ItemsRolledBack, legacy.ItemsNotProcessed, legacy.ItemsCancelled));
        Assert.Empty(await db.SourceExecutionItems.ToArrayAsync());
        Assert.Empty(await db.SourceExecutionItemSources.ToArrayAsync());
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseEnvironment("Testing")
            .UseSetting("ConnectionStrings:DefaultConnection", postgres.GetConnectionString()));
        using var client = factory.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
        var dto = (await client.GetFromJsonAsync<SourceExecutionDto>($"/api/source-executions/{execution}"))!;
        Assert.False(dto.HistoryAvailable);
        Assert.Equal(3, dto.ItemsIgnored); // Legacy convention is unchanged.
        await migrator.MigrateAsync(PreviousMigration);
        Assert.Equal(before, await LegacyJsonAsync());
        await db.Database.MigrateAsync();
        Assert.Empty(await db.SourceExecutionItems.ToArrayAsync());
        Assert.False(db.Database.HasPendingModelChanges());
    }

    [Fact]
    public async Task RolledBackDuplicatesKeepTheirProvisionalOutcomeWithoutLiveLinks()
    {
        var setup = await PrepareAsync();
        var result = await IngestAsync(setup, Item(), Item(), Item(null, null, company: "No existing company"));
        Assert.Equal(IngestionStatus.Conflict, result.Status);
        var history = await HistoryAsync(result.Error!.ExecutionId!.Value);
        Assert.Equal((1, 2, 0, 0), (history.ItemsRejected, history.ItemsRolledBack, history.ItemsNotProcessed, history.ItemsCancelled));
        var items = history.Items.OrderBy(x => x.ItemIndex).ToArray();
        Assert.Equal(Outcomes.Ignored, JsonNode.Parse(items[1].DecisionDetailsJson!)!["provisionalOutcomeCode"]!.GetValue<string>());
        Assert.All(items, x => { Assert.Null(x.OpportunityIdSnapshot); Assert.Empty(x.Sources); });
    }

    [Fact]
    public async Task DatabaseRejectsInvalidVersionsCountersHistoryAndCrossWorkspaceParentLinks()
    {
        var setup = await PrepareAsync();
        Assert.Equal(IngestionStatus.Succeeded,
            (await IngestAsync(setup, Item(), Item("second", "https://example.invalid/second"))).Status);
        await using var db = Db();
        // Independent statements: each failed statement rolls itself back, leaving the fixture valid.
        var invalidChecks = new[]
        {
            """UPDATE "SourceExecutions" SET "HistoryVersion" = 2""",
            """UPDATE "SourceExecutions" SET "HistoryVersion" = NULL""",
            """UPDATE "SourceExecutions" SET "ContractVersion" = NULL""",
            """UPDATE "SourceExecutions" SET "NormalizationVersion" = NULL""",
            """UPDATE "SourceExecutions" SET "TargetPipelineId" = NULL""",
            """UPDATE "SourceExecutions" SET "TargetPipelineStageId" = NULL""",
            """UPDATE "SourceExecutions" SET "ContextSnapshotJson" = NULL""",
            """UPDATE "SourceExecutions" SET "ContextSnapshotJson" = '[]'""",
            """UPDATE "SourceExecutions" SET "ItemsFound" = 3""",
            """UPDATE "SourceExecutions" SET "ItemsRejected" = -1""",
            """UPDATE "SourceExecutions" SET "ItemsRolledBack" = -1""",
            """UPDATE "SourceExecutions" SET "ItemsNotProcessed" = -1""",
            """UPDATE "SourceExecutions" SET "ItemsCancelled" = -1""",
            """UPDATE "SourceExecutions" SET "ItemsCreated" = 1, "ItemsRejected" = 1""",
            """UPDATE "SourceExecutions" SET "StatusCode" = 'failed'""",
            """UPDATE "SourceExecutions" SET "StatusCode" = 'cancelled'""",
            """UPDATE "SourceExecutionItems" SET "OutcomeCode" = 'unknown'""",
            """UPDATE "SourceExecutionItems" SET "ItemIndex" = -1""",
            """UPDATE "SourceExecutionItems" SET "ProcessedAt" = NULL""",
            """UPDATE "SourceExecutionItems" SET "ProcessedAt" = "ReceivedAt" - interval '1 second'""",
            """UPDATE "SourceExecutionItems" SET "OutcomeCode" = 'pending'""",
            """UPDATE "SourceExecutionItems" SET "CompanyName" = 'unpaired'""",
            """UPDATE "SourceExecutionItems" SET "NormalizedSourceUrl" = NULL""",
            """UPDATE "SourceExecutionItems" SET "OpportunityIdSnapshot" = NULL""",
            """UPDATE "SourceExecutionItems" SET "OpportunityIdSnapshot" = '00000000-0000-0000-0000-000000000001'""",
            """UPDATE "SourceExecutionItems" SET "OutcomeCode" = 'rolled-back'""",
            """UPDATE "SourceExecutionItems" SET "PayloadSnapshotJson" = '[]'""",
            """UPDATE "SourceExecutionItems" SET "DecisionDetailsJson" = 'null'""",
            """UPDATE "SourceExecutionItemSources" SET "RoleCode" = 'title-company'""",
            """UPDATE "SourceExecutionItemSources" SET "OpportunitySourceIdSnapshot" = '00000000-0000-0000-0000-000000000001'"""
        };
        foreach (var sql in invalidChecks)
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(sql));
            Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
        }
        foreach (var sql in new[]
        {
            """UPDATE "SourceExecutionItems" SET "ItemIndex" = 0""",
            """UPDATE "SourceExecutionItemSources" SET "RoleCode" = 'external-id'"""
        })
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(sql));
            Assert.Equal(PostgresErrorCodes.UniqueViolation, error.SqlState);
        }
        foreach (var table in new[] { "SourceExecutionItems", "SourceExecutionItemSources" })
        {
            var sql = $"""UPDATE "{table}" SET "WorkspaceId" = '00000000-0000-0000-0000-000000000001'""";
            var error = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(sql));
            Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, error.SqlState);
        }
        Assert.Equal(2, await db.SourceExecutionItems.CountAsync()); // Normalized title/company index is not unique.
    }

    [Fact]
    public async Task FreshReconstructionHasNoPendingMigrationOrModelChange()
    {
        await using var db = Db();
        Assert.Empty(await db.Database.GetAppliedMigrationsAsync());
        await db.Database.MigrateAsync();
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.False(db.Database.HasPendingModelChanges());
        Assert.EndsWith("_Phase73CollectionRetries", (await db.Database.GetAppliedMigrationsAsync()).Last());
        Assert.Empty(await db.SourceExecutionItems.ToArrayAsync());
        Assert.Empty(await db.SourceExecutionItemSources.ToArrayAsync());
    }
}
