using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using ProspectionCrm.Api.Data;
using ProspectionCrm.Api.Dtos.Ingestions;
using ProspectionCrm.Api.Dtos.SourceExecutions;
using ProspectionCrm.Api.Entities;
using ProspectionCrm.Api.Services;
using Testcontainers.PostgreSql;
using Xunit;

namespace ProspectionCrm.Api.Tests;

public sealed class IngestionTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer postgres = new PostgreSqlBuilder("postgres:18").Build();
    private WebApplicationFactory<Program> factory = null!;
    private HttpClient client = null!;
    private Setup setup = null!;
    private sealed record Setup(Guid WorkspaceId, Guid PipelineId, Guid StageId, Guid SourceId, Guid SearchId);
    private ProspectionCrmDbContext Db() => new(new DbContextOptionsBuilder<ProspectionCrmDbContext>()
        .UseNpgsql(postgres.GetConnectionString()).Options);
    private static string Route(Guid searchId) => $"/api/saved-searches/{searchId}/ingestions";

    public async Task InitializeAsync()
    {
        await postgres.StartAsync();
        await using var db = Db();
        await db.Database.MigrateAsync();
        setup = await SeedAsync(db);
        factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseEnvironment("Testing")
            .UseSetting("ConnectionStrings:DefaultConnection", postgres.GetConnectionString()));
        client = factory.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
    }
    public async Task DisposeAsync()
    {
        client?.Dispose();
        if (factory is not null) await factory.DisposeAsync();
        await postgres.DisposeAsync();
    }
    private static async Task<Setup> SeedAsync(ProspectionCrmDbContext db, bool archived = false)
    {
        var workspace = new Workspace { Name = "Ingestion", TimeZoneId = "UTC",
            OwnerUser = new UserAccount { Email = $"{Guid.NewGuid():N}@example.invalid" },
            ArchivedAt = archived ? DateTimeOffset.UtcNow : null };
        var pipeline = new Pipeline { Workspace = workspace, Name = "Destination", TypeCode = "custom" };
        var stage = new PipelineStage { Pipeline = pipeline, Name = "Explicit stage", CategoryCode = "active", SortOrder = 4 };
        var source = new SourceConfiguration { Workspace = workspace, Name = "Provided results", SourceTypeCode = "manual" };
        var search = new SavedSearch { Workspace = workspace, Pipeline = pipeline, SourceConfiguration = source, Name = "Search" };
        db.PipelineStages.Add(stage);
        db.SavedSearches.Add(search);
        await db.SaveChangesAsync();
        return new(workspace.Id, pipeline.Id, stage.Id, source.Id, search.Id);
    }
    private static IngestionItemRequest Item(string? external = "item-1", string? url = "https://example.invalid/jobs/1",
        string title = "Engineer", string? company = null) => new()
        { ExternalId = external, SourceUrl = url, Title = title, CompanyName = company, Location = "Bordeaux", Description = "Provided description" };
    private IngestionRequest Request(params IngestionItemRequest[] items) => new() { PipelineStageId = setup.StageId, Items = items.ToList() };
    private async Task<IngestionDto> IngestAsync(params IngestionItemRequest[] items)
    {
        using var response = await client.PostAsJsonAsync(Route(setup.SearchId), Request(items));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var result = (await response.Content.ReadFromJsonAsync<IngestionDto>())!;
        Assert.Equal($"https://localhost/api/source-executions/{result.Execution.Id}", response.Headers.Location!.ToString());
        var execution = (await client.GetFromJsonAsync<SourceExecutionDto>(response.Headers.Location))!;
        Assert.Equal(JsonSerializer.Serialize(result.Execution), JsonSerializer.Serialize(execution));
        Assert.Equal("succeeded", execution.StatusCode);
        Assert.Equal("manual", execution.TriggerTypeCode);
        Assert.Equal(items.Length, execution.ItemsFound);
        Assert.Equal(execution.ItemsFound, execution.ItemsCreated + execution.ItemsUpdated + execution.ItemsIgnored);
        Assert.True(execution.FinishedAt >= execution.StartedAt);
        Assert.Null(execution.ErrorMessage);
        return result;
    }
    private static async Task<JsonElement> ProblemAsync(HttpResponseMessage response, HttpStatusCode status, string? code = null)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal((int)status, problem.GetProperty("status").GetInt32());
        if (code is not null) Assert.Equal(code, problem.GetProperty("code").GetString());
        return problem;
    }
    private async Task<string> BusinessSnapshotAsync()
    {
        await using var db = Db();
        await using var connection = new NpgsqlConnection(postgres.GetConnectionString());
        await connection.OpenAsync();
        var snapshot = new SortedDictionary<string, string>();
        foreach (var table in db.Model.GetEntityTypes().Select(x => x.GetTableName()!).Distinct().Where(x => x != "SourceExecutions").Order())
        {
            await using var command = new NpgsqlCommand(
                $"SELECT COALESCE(jsonb_agg(to_jsonb(t) ORDER BY to_jsonb(t)::text), '[]'::jsonb)::text FROM \"{table}\" t", connection);
            snapshot[table] = (string)(await command.ExecuteScalarAsync())!;
        }
        return JsonSerializer.Serialize(snapshot);
    }

    [Fact]
    public async Task NewResultCreatesOnlyExecutionOpportunityAndProvenanceWithExactMapping()
    {
        var result = await IngestAsync(Item(title: "  Engineer  ", company: "Unknown studio"));
        Assert.Equal((1, 0, 0), (result.Execution.ItemsCreated, result.Execution.ItemsUpdated, result.Execution.ItemsIgnored));
        Assert.Equal("created", Assert.Single(result.Items).Outcome);
        await using var db = Db();
        var opportunity = await db.Opportunities.SingleAsync();
        Assert.Equal("  Engineer  ", opportunity.Title);
        Assert.Equal("normal", opportunity.PriorityCode);
        Assert.Equal("Bordeaux", opportunity.Location);
        Assert.Equal("Provided description", opportunity.Notes);
        Assert.Equal(setup.StageId, opportunity.PipelineStageId);
        Assert.Equal(setup.WorkspaceId, opportunity.WorkspaceId);
        Assert.Null(opportunity.CompanyId);
        Assert.Null(opportunity.Score);
        Assert.Null(opportunity.UpdatedAt);
        var source = await db.OpportunitySources.SingleAsync();
        Assert.Equal(opportunity.Id, source.OpportunityId);
        Assert.Equal(setup.SourceId, source.SourceConfigurationId);
        Assert.Equal(setup.SearchId, source.SavedSearchId);
        Assert.Equal(result.Execution.Id, source.SourceExecutionId);
        Assert.Equal("Provided results", source.SourceLabel);
        Assert.Equal("item-1", source.ExternalId);
        Assert.Equal("https://example.invalid/jobs/1", source.SourceUrl);
        Assert.Equal(source.FirstSeenAt, source.LastSeenAt);
        Assert.Empty(await db.Companies.ToArrayAsync());
        Assert.Null((await db.Workspaces.SingleAsync()).DefaultPipelineId);
        Assert.False(db.Database.HasPendingModelChanges());
    }

    [Fact]
    public async Task ExactReplayCreatesANewExecutionButNoNewOpportunityOrSource()
    {
        var first = await IngestAsync(Item());
        var second = await IngestAsync(Item());
        Assert.Equal(first.Items[0].OpportunityId, second.Items[0].OpportunityId);
        Assert.NotEqual(first.Execution.Id, second.Execution.Id);
        Assert.Equal((0, 1, 0), (second.Execution.ItemsCreated, second.Execution.ItemsUpdated, second.Execution.ItemsIgnored));
        await using var db = Db();
        Assert.Equal(1, await db.Opportunities.CountAsync());
        var source = await db.OpportunitySources.SingleAsync();
        Assert.Equal(first.Execution.Id, source.SourceExecutionId);
        Assert.True(source.LastSeenAt > source.FirstSeenAt);
        Assert.Equal(2, await db.SourceExecutions.CountAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RediscoveryPreservesAllOpportunityFieldsAndOriginalProvenance(bool archived)
    {
        var first = await IngestAsync(Item());
        await using var db = Db();
        var other = new Pipeline { WorkspaceId = setup.WorkspaceId, Name = "Moved by user", TypeCode = "business" };
        var otherStage = new PipelineStage { Pipeline = other, Name = "User stage", CategoryCode = "success" };
        db.PipelineStages.Add(otherStage);
        await db.SaveChangesAsync();
        var opportunity = await db.Opportunities.SingleAsync();
        opportunity.Title = "User title";
        opportunity.Notes = "User notes";
        opportunity.Location = "User location";
        opportunity.PriorityCode = "high";
        opportunity.Score = 73;
        opportunity.ScoredAt = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        opportunity.UpdatedAt = opportunity.ScoredAt;
        opportunity.PipelineStageId = otherStage.Id;
        opportunity.ArchivedAt = archived ? opportunity.ScoredAt : null;
        var source = await db.OpportunitySources.SingleAsync();
        source.FirstSeenAt = DateTimeOffset.Parse("2025-01-01T00:00:00Z");
        source.LastSeenAt = source.FirstSeenAt;
        source.SourceLabel = "Original label";
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var before = JsonSerializer.Serialize(await db.Opportunities.AsNoTracking().SingleAsync());
        var replay = await IngestAsync(Item(title: "Incoming new title"));
        Assert.Equal(first.Items[0].OpportunityId, replay.Items[0].OpportunityId);
        Assert.Equal(before, JsonSerializer.Serialize(await db.Opportunities.AsNoTracking().SingleAsync()));
        var afterSource = await db.OpportunitySources.AsNoTracking().SingleAsync();
        Assert.Equal(source.FirstSeenAt, afterSource.FirstSeenAt);
        Assert.True(afterSource.LastSeenAt > source.LastSeenAt);
        Assert.Equal("Original label", afterSource.SourceLabel);
        Assert.Equal(first.Execution.Id, afterSource.SourceExecutionId);
    }

    [Fact]
    public async Task DuplicateInsideLotIsIgnoredAndCountersCountInputItems()
    {
        var result = await IngestAsync(Item(), Item(title: "  ENGINEER  "));
        Assert.Equal((2, 1, 0, 1), (result.Execution.ItemsFound, result.Execution.ItemsCreated, result.Execution.ItemsUpdated, result.Execution.ItemsIgnored));
        Assert.Equal(new[] { "created", "ignored" }, result.Items.Select(x => x.Outcome));
        Assert.Single(result.Items.Select(x => x.OpportunityId).Distinct());
        await using var db = Db();
        Assert.Equal(1, await db.OpportunitySources.CountAsync());
    }

    [Fact]
    public async Task ExternalIdentityRediscoveryKeepsOldUrlAndAddsNewUrlAlias()
    {
        var first = await IngestAsync(Item());
        var second = await IngestAsync(Item(url: "https://example.invalid/new", title: "Different"));
        Assert.Equal(first.Items[0].OpportunityId, second.Items[0].OpportunityId);
        var third = await IngestAsync(Item(external: null, url: "https://example.invalid/new"));
        Assert.Equal(first.Items[0].OpportunityId, third.Items[0].OpportunityId);
        await using var db = Db();
        var sources = await db.OpportunitySources.ToArrayAsync();
        Assert.Equal(2, sources.Length);
        Assert.Single(sources, s => s.ExternalId == "item-1" && s.SourceUrl == "https://example.invalid/jobs/1");
        Assert.Single(sources, s => s.ExternalId is null && s.SourceUrl == "https://example.invalid/new");
    }

    [Fact]
    public async Task UrlNormalizationMatchesExistingRawSourcesAcrossConfigurations()
    {
        var first = await IngestAsync(Item());
        await using var db = Db();
        (await db.OpportunitySources.SingleAsync()).SourceUrl = "HTTPS://EXAMPLE.INVALID/jobs/1";
        var configuration = new SourceConfiguration { WorkspaceId = setup.WorkspaceId, Name = "Second", SourceTypeCode = "manual" };
        db.SourceConfigurations.Add(configuration);
        var search = new SavedSearch { WorkspaceId = setup.WorkspaceId, PipelineId = setup.PipelineId, SourceConfiguration = configuration, Name = "Other search" };
        db.SavedSearches.Add(search);
        await db.SaveChangesAsync();
        setup = setup with { SourceId = configuration.Id, SearchId = search.Id };
        var next = await IngestAsync(Item(external: "second-id", url: " https://example.invalid/jobs/1 ", title: "New title"));
        Assert.Equal(first.Items[0].OpportunityId, next.Items[0].OpportunityId);
        Assert.Equal(1, await db.Opportunities.CountAsync());
        var sources = await db.OpportunitySources.AsNoTracking().ToArrayAsync();
        Assert.Equal(2, sources.Length);
        Assert.Single(sources, s => s.ExternalId == "second-id" && s.SourceUrl is null && s.SourceConfigurationId == configuration.Id);
        Assert.Single(sources, s => s.SourceUrl == "HTTPS://EXAMPLE.INVALID/jobs/1" && s.SourceExecutionId == first.Execution.Id);
    }

    [Theory]
    [InlineData("https://example.invalid/jobs/A?x=1&y=2#Top", "https://example.invalid/jobs/a?x=1&y=2#Top")]
    [InlineData("https://example.invalid/?x=1&y=2", "https://example.invalid/?y=2&x=1")]
    [InlineData("https://example.invalid/#Top", "https://example.invalid/#Other")]
    [InlineData("https://example.invalid/a%2Fb", "https://example.invalid/a/b")]
    [InlineData("https://example.invalid:443/", "https://example.invalid/")]
    public async Task ConservativeUrlsKeepPathQueryFragmentEscapesAndExplicitPort(string firstUrl, string secondUrl)
    {
        var result = await IngestAsync(Item(null, firstUrl), Item(null, secondUrl));
        Assert.Equal(2, result.Execution.ItemsCreated);
        await using var db = Db();
        Assert.Equal(new[] { firstUrl, secondUrl }.Order(), (await db.OpportunitySources.Select(x => x.SourceUrl).ToArrayAsync()).Order());
    }

    private async Task<Guid> ManualOpportunityAsync(string companyName, string title = "Unity Engineer")
    {
        await using var db = Db();
        var company = new Company { WorkspaceId = setup.WorkspaceId, Name = companyName };
        var opportunity = new Opportunity { WorkspaceId = setup.WorkspaceId, PipelineStageId = setup.StageId, Company = company, Title = title, PriorityCode = "low" };
        db.Opportunities.Add(opportunity);
        await db.SaveChangesAsync();
        return opportunity.Id;
    }

    [Fact]
    public async Task TitleCompanyFallbackUsesNormalizedComparisonWithoutMutatingBusinessText()
    {
        var id = await ManualOpportunityAsync("  Crew\t Rats ", " Unity  Engineer ");
        var result = await IngestAsync(Item(null, null, "UNITY\tENGINEER", "crew rats"));
        Assert.Equal(id, result.Items[0].OpportunityId);
        Assert.Equal(1, result.Execution.ItemsUpdated);
        await using var db = Db();
        Assert.Equal(" Unity  Engineer ", (await db.Opportunities.SingleAsync()).Title);
        Assert.Equal("  Crew\t Rats ", (await db.Companies.SingleAsync()).Name);
        var replay = await IngestAsync(Item(null, null, "Unity Engineer", "Crew Rats"));
        Assert.Equal(id, replay.Items[0].OpportunityId);
        Assert.Equal(1, await db.OpportunitySources.CountAsync());
    }

    [Fact]
    public async Task SameTitleDifferentCompanyDoesNotMatchAndNoCompanyIsCreatedOrAssigned()
    {
        var original = await ManualOpportunityAsync("Studio A");
        var result = await IngestAsync(Item(title: "Unity Engineer", company: "Studio B"));
        Assert.NotEqual(original, result.Items[0].OpportunityId);
        await using var db = Db();
        Assert.Equal(1, await db.Companies.CountAsync());
        Assert.Null((await db.Opportunities.SingleAsync(x => x.Id == result.Items[0].OpportunityId)).CompanyId);
    }

    [Fact]
    public async Task DuplicateCompanyNamesNeverChooseAnArbitraryOpportunity()
    {
        await ManualOpportunityAsync("Same Studio");
        await ManualOpportunityAsync(" same  studio ");
        var before = await BusinessSnapshotAsync();
        using var response = await client.PostAsJsonAsync(Route(setup.SearchId), Request(Item(null, null, "Unity Engineer", "SAME STUDIO")));
        await ProblemAsync(response, HttpStatusCode.Conflict, "AmbiguousIdentity");
        Assert.Equal(before, await BusinessSnapshotAsync());
    }

    [Fact]
    public async Task NewTitleCompanyOnlyCannotInventPersistentIdentity()
    {
        using var response = await client.PostAsJsonAsync(Route(setup.SearchId), Request(Item(null, null, company: "Unknown")));
        await ProblemAsync(response, HttpStatusCode.Conflict, "MissingPersistentIdentity");
        await using var db = Db();
        Assert.Empty(await db.Opportunities.ToArrayAsync());
        Assert.Empty(await db.Companies.ToArrayAsync());
        var execution = await db.SourceExecutions.SingleAsync();
        Assert.Equal("failed", execution.StatusCode);
        Assert.Equal((1, 0, 0, 1), (execution.ItemsFound, execution.ItemsCreated, execution.ItemsUpdated, execution.ItemsIgnored));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ContradictoryIdentitiesRollBackEarlierCreationsAndProvenanceUpdates(bool companyConflict)
    {
        var initial = await IngestAsync(Item());
        if (companyConflict) await ManualOpportunityAsync("Studio");
        else await IngestAsync(Item("item-2", "https://example.invalid/jobs/2"));
        var before = await BusinessSnapshotAsync();
        var conflict = companyConflict ? Item("item-1", null, "Unity Engineer", "Studio")
            : Item("item-1", "https://example.invalid/jobs/2");
        using var response = await client.PostAsJsonAsync(Route(setup.SearchId), Request(
            Item(), Item("new-before-conflict", "https://example.invalid/new"), conflict));
        var problem = await ProblemAsync(response, HttpStatusCode.Conflict, "AmbiguousIdentity");
        Assert.Equal(2, problem.GetProperty("itemIndex").GetInt32());
        Assert.Equal(before, await BusinessSnapshotAsync());
        var failed = (await client.GetFromJsonAsync<SourceExecutionDto>($"/api/source-executions/{problem.GetProperty("executionId").GetGuid()}"))!;
        Assert.Equal("failed", failed.StatusCode);
        Assert.Equal((3, 0, 0, 3), (failed.ItemsFound, failed.ItemsCreated, failed.ItemsUpdated, failed.ItemsIgnored));
        Assert.True(failed.FinishedAt >= failed.StartedAt);
        Assert.Equal("AmbiguousIdentity", failed.ErrorMessage);
    }

    [Theory]
    [InlineData("search-archived")]
    [InlineData("search-disabled")]
    [InlineData("configuration-archived")]
    [InlineData("configuration-disabled")]
    [InlineData("pipeline-archived")]
    [InlineData("stage-archived")]
    public async Task NonExecutableStateReturns409WithoutStartingExecution(string state)
    {
        await using var db = Db();
        switch (state)
        {
            case "search-archived": (await db.SavedSearches.SingleAsync()).ArchivedAt = DateTimeOffset.UtcNow; break;
            case "search-disabled": (await db.SavedSearches.SingleAsync()).Enabled = false; break;
            case "configuration-archived": (await db.SourceConfigurations.SingleAsync()).ArchivedAt = DateTimeOffset.UtcNow; break;
            case "configuration-disabled": (await db.SourceConfigurations.SingleAsync()).Enabled = false; break;
            case "pipeline-archived": (await db.Pipelines.SingleAsync()).ArchivedAt = DateTimeOffset.UtcNow; break;
            case "stage-archived": (await db.PipelineStages.SingleAsync()).ArchivedAt = DateTimeOffset.UtcNow; break;
        }
        await db.SaveChangesAsync();
        var before = await BusinessSnapshotAsync();
        using var response = await client.PostAsJsonAsync(Route(setup.SearchId), Request(Item()));
        await ProblemAsync(response, HttpStatusCode.Conflict, "InactiveResource");
        Assert.Equal(before, await BusinessSnapshotAsync());
        Assert.Empty(await db.SourceExecutions.ToArrayAsync());
    }

    [Fact]
    public async Task WrongPipelineIs409AndMissingOrForeignResourcesAre404()
    {
        await using var db = Db();
        var other = new PipelineStage { Pipeline = new Pipeline { WorkspaceId = setup.WorkspaceId, Name = "Other", TypeCode = "custom" }, Name = "Other", CategoryCode = "active" };
        db.PipelineStages.Add(other);
        await db.SaveChangesAsync();
        using var wrong = await client.PostAsJsonAsync(Route(setup.SearchId), new IngestionRequest { PipelineStageId = other.Id, Items = [Item()] });
        await ProblemAsync(wrong, HttpStatusCode.Conflict, "WrongPipeline");
        var foreign = await SeedAsync(db, archived: true);
        foreach (var pair in new[] { (Guid.NewGuid(), setup.StageId), (setup.SearchId, Guid.NewGuid()), (foreign.SearchId, foreign.StageId), (setup.SearchId, foreign.StageId) })
        {
            using var response = await client.PostAsJsonAsync(Route(pair.Item1), new IngestionRequest { PipelineStageId = pair.Item2, Items = [Item()] });
            await ProblemAsync(response, HttpStatusCode.NotFound, "ResourceNotFound");
        }
        Assert.Empty(await db.SourceExecutions.ToArrayAsync());
        Assert.Empty(await db.Opportunities.ToArrayAsync());
    }

    [Fact]
    public async Task ForeignOpportunityUrlAndTitleAreNotDeduplicationCandidates()
    {
        await using var db = Db();
        var foreign = await SeedAsync(db, archived: true);
        var opportunity = new Opportunity { WorkspaceId = foreign.WorkspaceId, PipelineStageId = foreign.StageId, Title = "Engineer", PriorityCode = "normal" };
        db.OpportunitySources.Add(new OpportunitySource { Opportunity = opportunity, SourceConfigurationId = foreign.SourceId, SourceLabel = "Foreign", ExternalId = "item-1", SourceUrl = "https://example.invalid/jobs/1" });
        await db.SaveChangesAsync();
        var result = await IngestAsync(Item());
        Assert.NotEqual(opportunity.Id, result.Items[0].OpportunityId);
        Assert.Equal(2, await db.Opportunities.CountAsync());
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("null-items")]
    [InlineData("null-item")]
    [InlineData("empty-stage")]
    [InlineData("blank-title")]
    [InlineData("long-title")]
    [InlineData("no-identity")]
    [InlineData("url-credentials")]
    [InlineData("url-relative")]
    [InlineData("url-backslash")]
    [InlineData("too-many")]
    public async Task InvalidLotIs400WithoutWrites(string kind)
    {
        var items = new List<IngestionItemRequest> { Item() };
        switch (kind)
        {
            case "empty": items.Clear(); break;
            case "null-item": items.Add(null!); break;
            case "blank-title": items.Add(Item(title: " ")); break;
            case "long-title": items.Add(Item(title: new string('a', 201))); break;
            case "no-identity": items.Add(Item(null, null)); break;
            case "url-credentials": items.Add(Item(url: "https://user:password@example.invalid/")); break;
            case "url-relative": items.Add(Item(url: "/relative")); break;
            case "url-backslash": items.Add(Item(url: "https://example.invalid/a\\b")); break;
            case "too-many": items = Enumerable.Repeat(Item(), 101).ToList(); break;
        }
        using var response = await client.PostAsJsonAsync(Route(setup.SearchId), new IngestionRequest
        { PipelineStageId = kind == "empty-stage" ? Guid.Empty : setup.StageId, Items = kind == "null-items" ? null : items });
        await ProblemAsync(response, HttpStatusCode.BadRequest);
        await using var db = Db();
        Assert.Empty(await db.SourceExecutions.ToArrayAsync());
        Assert.Empty(await db.Opportunities.ToArrayAsync());
    }

    [Fact]
    public async Task UnknownFieldsAndMalformedJsonAreRejected()
    {
        foreach (var json in new[] { "{", $$"""{"pipelineStageId":"{{setup.StageId}}","items":[{"title":"Job","externalId":"x","publishedAt":"2026-01-01"}]}""" })
        {
            using var response = await client.PostAsync(Route(setup.SearchId), new StringContent(json, Encoding.UTF8, "application/json"));
            await ProblemAsync(response, HttpStatusCode.BadRequest);
        }
        await using var db = Db();
        Assert.Empty(await db.SourceExecutions.ToArrayAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TechnicalFailureRollsBackBusinessButPersistsFailedExecution(bool failFinalStatus)
    {
        await IngestAsync(Item());
        var before = await BusinessSnapshotAsync();
        await using var db = Db();
        await db.Database.ExecuteSqlRawAsync(failFinalStatus
            ? "ALTER TABLE \"SourceExecutions\" ADD CONSTRAINT reject_test_success CHECK (\"StatusCode\" <> 'succeeded') NOT VALID"
            : "ALTER TABLE \"OpportunitySources\" ADD CONSTRAINT reject_test_source CHECK (\"ExternalId\" <> 'reject')");
        using var response = await client.PostAsJsonAsync(Route(setup.SearchId), Request(Item(), Item("new", "https://example.invalid/new"), Item("reject", "https://example.invalid/reject")));
        var problem = await ProblemAsync(response, HttpStatusCode.InternalServerError, "PersistenceFailure");
        Assert.Equal(before, await BusinessSnapshotAsync());
        var execution = await db.SourceExecutions.SingleAsync(x => x.Id == problem.GetProperty("executionId").GetGuid());
        Assert.Equal("failed", execution.StatusCode);
        Assert.Equal((3, 0, 0, 3), (execution.ItemsFound, execution.ItemsCreated, execution.ItemsUpdated, execution.ItemsIgnored));
        Assert.Equal("PersistenceFailure", execution.ErrorMessage);
        Assert.DoesNotContain("reject_test_source", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentHttpIngestionsSerializeDecisionAndCreateExactlyOneOpportunity(bool urlOnly)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var token = timeout.Token;
        await using var blocker = Db();
        await using var transaction = await blocker.Database.BeginTransactionAsync(token);
        var lockKey = BinaryPrimitives.ReadInt64BigEndian(SHA256.HashData(Encoding.UTF8.GetBytes($"ProspectionCrm/manual-ingestion/{setup.WorkspaceId:D}")));
        await blocker.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({lockKey})", token);
        var item = urlOnly ? Item(null) : Item(url: null);
        var first = client.PostAsJsonAsync(Route(setup.SearchId), Request(item), token);
        var second = client.PostAsJsonAsync(Route(setup.SearchId), Request(item), token);
        await using var connection = new NpgsqlConnection(postgres.GetConnectionString());
        await connection.OpenAsync(token);
        await using var command = new NpgsqlCommand("SELECT count(*) FROM pg_stat_activity WHERE datname = current_database() AND wait_event_type = 'Lock' AND query LIKE '%pg_advisory_xact_lock%'", connection);
        while (Convert.ToInt64(await command.ExecuteScalarAsync(token)) < 2) await Task.Delay(25, token);
        Assert.False(first.IsCompleted);
        Assert.False(second.IsCompleted);
        await transaction.CommitAsync(token);
        var responses = await Task.WhenAll(first, second);
        var results = new List<IngestionDto>();
        foreach (var response in responses)
        {
            using (response)
            {
                Assert.Equal(HttpStatusCode.Created, response.StatusCode);
                results.Add((await response.Content.ReadFromJsonAsync<IngestionDto>(token))!);
            }
        }
        Assert.Equal(new[] { 0, 1 }, results.Select(x => x.Execution.ItemsCreated).Order());
        Assert.Equal(new[] { 0, 1 }, results.Select(x => x.Execution.ItemsUpdated).Order());
        Assert.Single(results.Select(x => x.Items[0].OpportunityId).Distinct());
        await using var db = Db();
        Assert.Equal(1, await db.Opportunities.CountAsync(token));
        Assert.Equal(1, await db.OpportunitySources.CountAsync(token));
        Assert.Equal(2, await db.SourceExecutions.CountAsync(x => x.StatusCode == "succeeded", token));
    }

    [Theory]
    [InlineData("Pipeline")]
    [InlineData("SourceConfiguration")]
    [InlineData("SavedSearch")]
    public async Task WaitingIngestionRevalidatesCommittedArchiveOrDeactivation(string resource)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var token = timeout.Token;
        await using var blocker = Db();
        await using var transaction = await blocker.Database.BeginTransactionAsync(token);
        if (resource == "Pipeline")
            await blocker.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"Pipelines\" WHERE \"Id\" = {setup.PipelineId} FOR UPDATE", token);
        else if (resource == "SourceConfiguration")
            await blocker.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"SourceConfigurations\" WHERE \"Id\" = {setup.SourceId} FOR UPDATE", token);
        else
            await blocker.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"SavedSearches\" WHERE \"Id\" = {setup.SearchId} FOR UPDATE", token);
        var ingestion = client.PostAsJsonAsync(Route(setup.SearchId), Request(Item()), token);
        await using var connection = new NpgsqlConnection(postgres.GetConnectionString());
        await connection.OpenAsync(token);
        await using var command = new NpgsqlCommand("SELECT count(*) FROM pg_stat_activity WHERE datname = current_database() AND wait_event_type = 'Lock' AND query LIKE '%FOR SHARE%'", connection);
        while (Convert.ToInt64(await command.ExecuteScalarAsync(token)) < 1) await Task.Delay(25, token);
        if (resource == "Pipeline")
            await blocker.PipelineStages.Where(x => x.Id == setup.StageId).ExecuteUpdateAsync(s => s.SetProperty(x => x.ArchivedAt, DateTimeOffset.UtcNow), token);
        else if (resource == "SourceConfiguration")
            await blocker.SourceConfigurations.Where(x => x.Id == setup.SourceId).ExecuteUpdateAsync(s => s.SetProperty(x => x.Enabled, false), token);
        else
            await blocker.SavedSearches.Where(x => x.Id == setup.SearchId).ExecuteUpdateAsync(s => s.SetProperty(x => x.Enabled, false), token);
        await transaction.CommitAsync(token);
        using var response = await ingestion;
        await ProblemAsync(response, HttpStatusCode.Conflict, "InactiveResource");
        await using var db = Db();
        Assert.Empty(await db.SourceExecutions.ToArrayAsync(token));
        Assert.Empty(await db.Opportunities.ToArrayAsync(token));
    }

    private sealed class FixedWorkspaceProvider(Guid workspaceId) : ICurrentWorkspaceProvider
    {
        public Task<Guid> GetCurrentWorkspaceIdAsync(CancellationToken cancellationToken = default) => Task.FromResult(workspaceId);
    }

    [Fact]
    public async Task WorkspaceIngestionLockDoesNotBlockAnotherWorkspace()
    {
        await using var db = Db();
        var other = await SeedAsync(db);
        // V1 normally selects one active workspace. A fixed provider isolates the locking
        // guarantee without changing production workspace selection or using a global lock.
        using var otherFactory = factory.WithWebHostBuilder(b => b.ConfigureServices(services =>
        {
            services.RemoveAll<ICurrentWorkspaceProvider>();
            services.AddSingleton<ICurrentWorkspaceProvider>(new FixedWorkspaceProvider(other.WorkspaceId));
        }));
        using var otherClient = otherFactory.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
        await using var transaction = await db.Database.BeginTransactionAsync();
        var lockKey = BinaryPrimitives.ReadInt64BigEndian(SHA256.HashData(Encoding.UTF8.GetBytes($"ProspectionCrm/manual-ingestion/{setup.WorkspaceId:D}")));
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({lockKey})");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var response = await otherClient.PostAsJsonAsync(Route(other.SearchId), new IngestionRequest
            { PipelineStageId = other.StageId, Items = [Item()] }, timeout.Token);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        await transaction.RollbackAsync();
        var opportunity = await db.Opportunities.AsNoTracking().SingleAsync();
        Assert.Equal(other.WorkspaceId, opportunity.WorkspaceId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AbsentOrAmbiguousWorkspaceIs409WithoutWrites(bool multiple)
    {
        await using var db = Db();
        if (multiple) await SeedAsync(db);
        else await db.Workspaces.ExecuteUpdateAsync(s => s.SetProperty(x => x.ArchivedAt, DateTimeOffset.UtcNow));
        using var response = await client.PostAsJsonAsync(Route(setup.SearchId), Request(Item()));
        await ProblemAsync(response, HttpStatusCode.Conflict, "WorkspaceUnavailable");
        Assert.Empty(await db.SourceExecutions.ToArrayAsync());
    }
}
