using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using ProspectionCrm.Api.Data;
using ProspectionCrm.Api.Dtos.Opportunities;
using ProspectionCrm.Api.Dtos.Pipelines;
using ProspectionCrm.Api.Entities;
using ProspectionCrm.Api.Services;
using Testcontainers.PostgreSql;
using Xunit;

namespace ProspectionCrm.Api.Tests;

// A disposable PostgreSQL per case, as in Phases 4.1/4.2. No development settings or volumes.
public sealed class PipelineStageTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer postgres = new PostgreSqlBuilder("postgres:18").Build();
    private WebApplicationFactory<Program> factory = null!;
    private HttpClient client = null!;
    private Guid workspaceId;
    private Guid pipelineId;
    private string Route => $"/api/pipelines/{pipelineId}/stages";

    public async Task InitializeAsync()
    {
        await postgres.StartAsync();
        await using var db = CreateContext();
        await db.Database.MigrateAsync();
        workspaceId = (await new BootstrapService(db).BootstrapAsync(default)).Bootstrap!.WorkspaceId;
        var pipeline = new Pipeline { WorkspaceId = workspaceId, Name = "Parent", TypeCode = "custom" };
        db.Pipelines.Add(pipeline);
        await db.SaveChangesAsync();
        pipelineId = pipeline.Id;
        factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.UseEnvironment("Testing")
                .UseSetting("ConnectionStrings:DefaultConnection", postgres.GetConnectionString()));
        client = factory.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
    }

    public async Task DisposeAsync()
    {
        client?.Dispose();
        if (factory is not null)
            await factory.DisposeAsync();
        await postgres.DisposeAsync();
    }

    private ProspectionCrmDbContext CreateContext() => new(
        new DbContextOptionsBuilder<ProspectionCrmDbContext>().UseNpgsql(postgres.GetConnectionString()).Options);

    private async Task<PipelineStageDto> CreateAsync(string name = "Stage", string category = "active")
    {
        using var response = await client.PostAsJsonAsync(Route, new { Name = name, CategoryCode = category });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var stage = (await response.Content.ReadFromJsonAsync<PipelineStageDto>())!;
        Assert.EndsWith($"{Route}/{stage.Id}", response.Headers.Location?.ToString());
        return stage;
    }

    private async Task<PipelineStageDto> GetAsync(Guid id)
        => (await client.GetFromJsonAsync<PipelineStageDto>($"{Route}/{id}"))!;

    private async Task ActionAsync(Guid id, string action, HttpStatusCode expected = HttpStatusCode.NoContent)
    {
        using var response = await client.PostAsync($"{Route}/{id}/{action}", null);
        Assert.Equal(expected, response.StatusCode);
    }

    [Theory]
    [InlineData("active")]
    [InlineData("success")]
    [InlineData("failure")]
    public async Task FirstStageAcceptsCategoryTrimsNameAndIgnoresProtectedFields(string category)
    {
        await using var db = CreateContext();
        var other = new Pipeline { WorkspaceId = workspaceId, Name = "Other", TypeCode = "custom" };
        db.Pipelines.Add(other);
        await db.SaveChangesAsync();
        Assert.Empty((await client.GetFromJsonAsync<List<PipelineStageDto>>(Route))!);
        using var response = await client.PostAsJsonAsync(Route, new
        {
            Name = "  First  ", Description = "Description", CategoryCode = category,
            PipelineId = other.Id, SortOrder = 90, ArchivedAt = DateTimeOffset.UtcNow,
            Opportunities = new[] { new { Title = "Not created" } }
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = (await response.Content.ReadFromJsonAsync<PipelineStageDto>())!;
        Assert.EndsWith($"{Route}/{created.Id}", response.Headers.Location?.ToString());
        Assert.NotEqual(Guid.Empty, created.Id);
        Assert.Equal(pipelineId, created.PipelineId);
        Assert.Equal("First", created.Name);
        Assert.Equal("Description", created.Description);
        Assert.Equal(category, created.CategoryCode);
        Assert.Equal(0, created.SortOrder);
        Assert.Null(created.ArchivedAt);
        Assert.Equal(created.Id, (await GetAsync(created.Id)).Id);
        var saved = Assert.Single(await db.PipelineStages.AsNoTracking().ToListAsync());
        Assert.Equal(pipelineId, saved.PipelineId);
        Assert.Empty(await db.Opportunities.ToListAsync());
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("blank")]
    [InlineData("null-name")]
    [InlineData("long-name")]
    [InlineData("long-description")]
    [InlineData("invalid-category")]
    [InlineData("null-category")]
    [InlineData("wrong-case-category")]
    public async Task InvalidCreateAndUpdateReturn400WithoutWriting(string invalid)
    {
        var existing = await CreateAsync();
        var body = new
        {
            Name = invalid switch { "empty" => "", "blank" => " \t ", "null-name" => null, "long-name" => new string('n', 201), _ => "Valid" },
            Description = invalid == "long-description" ? new string('d', 2001) : null,
            CategoryCode = invalid switch { "invalid-category" => "unknown", "null-category" => null, "wrong-case-category" => "Active", _ => "active" }
        };
        using var create = await client.PostAsJsonAsync(Route, body);
        using var update = await client.PutAsJsonAsync($"{Route}/{existing.Id}", body);
        Assert.Equal(HttpStatusCode.BadRequest, create.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, update.StatusCode);
        await using var db = CreateContext();
        var saved = Assert.Single(await db.PipelineStages.AsNoTracking().ToListAsync());
        Assert.Equal("Stage", saved.Name);
        Assert.Null(saved.UpdatedAt);
    }

    [Fact]
    public async Task AppendKeepsArchivedPositionsAndListsAreOrdered()
    {
        var first = await CreateAsync();
        var second = await CreateAsync();
        Assert.Equal(0, first.SortOrder);
        Assert.Equal(1, second.SortOrder);
        await ActionAsync(second.Id, "archive");
        var third = await CreateAsync();
        Assert.Equal(2, third.SortOrder);
        var active = (await client.GetFromJsonAsync<List<PipelineStageDto>>(Route))!;
        Assert.Equal(new[] { first.Id, third.Id }, active.Select(x => x.Id));
        var all = (await client.GetFromJsonAsync<List<PipelineStageDto>>($"{Route}?includeArchived=true"))!;
        Assert.Equal(new[] { first.Id, second.Id, third.Id }, all.Select(x => x.Id));
        Assert.NotNull((await GetAsync(second.Id)).ArchivedAt);
        await ActionAsync(second.Id, "restore");
        Assert.Equal(1, (await GetAsync(second.Id)).SortOrder);
        Assert.Equal(3, (await CreateAsync()).SortOrder);
        // Existing gaps are not filled, including positions held by archived stages.
        await using var db = CreateContext();
        db.PipelineStages.Add(new PipelineStage
        {
            PipelineId = pipelineId, Name = "Legacy gap", CategoryCode = "active", SortOrder = 10, ArchivedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
        Assert.Equal(11, (await CreateAsync()).SortOrder);
    }

    [Fact]
    public async Task UpdateAcceptsBoundariesButCannotMoveReorderOrArchive()
    {
        var stage = await CreateAsync();
        using var response = await client.PutAsJsonAsync($"{Route}/{stage.Id}", new
        {
            Name = "  " + new string('n', 200) + "  ", Description = new string('d', 2000), CategoryCode = "success",
            PipelineId = Guid.NewGuid(), SortOrder = 42, ArchivedAt = DateTimeOffset.UtcNow,
            Opportunities = new[] { new { Title = "Not created" } }
        });
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var updated = await GetAsync(stage.Id);
        Assert.Equal(new string('n', 200), updated.Name);
        Assert.Equal(new string('d', 2000), updated.Description);
        Assert.Equal("success", updated.CategoryCode);
        Assert.Equal(pipelineId, updated.PipelineId);
        Assert.Equal(stage.SortOrder, updated.SortOrder);
        Assert.Null(updated.ArchivedAt);
        Assert.NotNull(updated.UpdatedAt);
        await using var db = CreateContext();
        Assert.Empty(await db.Opportunities.ToListAsync());
    }

    [Fact]
    public async Task ArchiveAndRestorePreserveOtherStagesAndOpportunities()
    {
        var stage = await CreateAsync();
        var other = await CreateAsync("Other");
        await using var db = CreateContext();
        var opportunity = new Opportunity { WorkspaceId = workspaceId, PipelineStageId = stage.Id, Title = "History", PriorityCode = "normal" };
        var otherOpportunity = new Opportunity { WorkspaceId = workspaceId, PipelineStageId = other.Id, Title = "Other", PriorityCode = "normal" };
        db.Opportunities.AddRange(opportunity, otherOpportunity);
        await db.SaveChangesAsync();
        var before = await OpportunitySnapshotAsync(db);
        var otherBefore = JsonSerializer.Serialize(await GetAsync(other.Id));
        await ActionAsync(stage.Id, "archive");
        var archived = await GetAsync(stage.Id);
        Assert.NotNull(archived.ArchivedAt);
        Assert.Equal(stage.SortOrder, archived.SortOrder);
        await ActionAsync(stage.Id, "archive");
        Assert.Equal(JsonSerializer.Serialize(archived), JsonSerializer.Serialize(await GetAsync(stage.Id)));
        using var rejectedUpdate = await client.PutAsJsonAsync($"{Route}/{stage.Id}", new { Name = "Rejected", CategoryCode = "failure" });
        Assert.Equal(HttpStatusCode.Conflict, rejectedUpdate.StatusCode);
        Assert.Equal(before, await OpportunitySnapshotAsync(db));
        Assert.Equal(otherBefore, JsonSerializer.Serialize(await GetAsync(other.Id)));
        await ActionAsync(stage.Id, "restore");
        var restored = await GetAsync(stage.Id);
        Assert.Null(restored.ArchivedAt);
        Assert.Equal(stage.SortOrder, restored.SortOrder);
        await ActionAsync(stage.Id, "restore");
        Assert.Equal(JsonSerializer.Serialize(restored), JsonSerializer.Serialize(await GetAsync(stage.Id)));
        Assert.Equal(before, await OpportunitySnapshotAsync(db));
        Assert.Equal(otherBefore, JsonSerializer.Serialize(await GetAsync(other.Id)));
        using var update = await client.PutAsJsonAsync($"{Route}/{stage.Id}", new { Name = "Restored and editable", CategoryCode = "failure" });
        Assert.Equal(HttpStatusCode.NoContent, update.StatusCode);
        using var delete = await client.DeleteAsync($"{Route}/{stage.Id}");
        Assert.Equal(HttpStatusCode.MethodNotAllowed, delete.StatusCode);
    }

    [Fact]
    public async Task ArchivedParentAllowsReadsButRejectsEveryWrite()
    {
        var active = await CreateAsync();
        var archived = await CreateAsync();
        await ActionAsync(archived.Id, "archive");
        using var parentArchive = await client.PostAsync($"/api/pipelines/{pipelineId}/archive", null);
        Assert.Equal(HttpStatusCode.NoContent, parentArchive.StatusCode);
        var before = JsonSerializer.Serialize(await client.GetFromJsonAsync<List<PipelineStageDto>>($"{Route}?includeArchived=true"));
        Assert.Single((await client.GetFromJsonAsync<List<PipelineStageDto>>(Route))!);
        Assert.Null((await GetAsync(active.Id)).ArchivedAt);
        Assert.NotNull((await GetAsync(archived.Id)).ArchivedAt);
        using var create = await client.PostAsJsonAsync(Route, new { Name = "Rejected", CategoryCode = "active" });
        Assert.Equal(HttpStatusCode.Conflict, create.StatusCode);
        foreach (var stage in new[] { active, archived })
        {
            using var update = await client.PutAsJsonAsync($"{Route}/{stage.Id}", new { Name = "Rejected", CategoryCode = "success" });
            Assert.Equal(HttpStatusCode.Conflict, update.StatusCode);
            await ActionAsync(stage.Id, "archive", HttpStatusCode.Conflict);
            await ActionAsync(stage.Id, "restore", HttpStatusCode.Conflict);
        }
        Assert.Equal(before, JsonSerializer.Serialize(await client.GetFromJsonAsync<List<PipelineStageDto>>($"{Route}?includeArchived=true")));
        using var parentRestore = await client.PostAsync($"/api/pipelines/{pipelineId}/restore", null);
        Assert.Equal(HttpStatusCode.NoContent, parentRestore.StatusCode);
        await ActionAsync(archived.Id, "restore");
        Assert.Equal(2, (await CreateAsync()).SortOrder);
    }

    [Fact]
    public async Task MissingForeignAndWrongParentResourcesReturn404()
    {
        var localStage = await CreateAsync();
        await using var db = CreateContext();
        var otherWorkspace = new Workspace
        {
            OwnerUserId = (await db.UserAccounts.SingleAsync()).Id, Name = "Foreign", TimeZoneId = "UTC", ArchivedAt = DateTimeOffset.UtcNow
        };
        var foreignPipeline = new Pipeline { Workspace = otherWorkspace, Name = "Foreign", TypeCode = "custom" };
        var otherPipeline = new Pipeline { WorkspaceId = workspaceId, Name = "Other", TypeCode = "custom" };
        var foreignStage = new PipelineStage { Pipeline = foreignPipeline, Name = "Foreign", CategoryCode = "active" };
        var otherStage = new PipelineStage { Pipeline = otherPipeline, Name = "Other", CategoryCode = "active" };
        db.PipelineStages.AddRange(foreignStage, otherStage);
        await db.SaveChangesAsync();
        foreach (var id in new[] { foreignPipeline.Id, Guid.NewGuid() })
        {
            using var list = await client.GetAsync($"/api/pipelines/{id}/stages?includeArchived=true");
            Assert.Equal(HttpStatusCode.NotFound, list.StatusCode);
            using var create = await client.PostAsJsonAsync($"/api/pipelines/{id}/stages", new { Name = "Rejected", CategoryCode = "active" });
            Assert.Equal(HttpStatusCode.NotFound, create.StatusCode);
        }
        foreach (var pair in new[]
        {
            (foreignPipeline.Id, foreignStage.Id), (pipelineId, foreignStage.Id),
            (pipelineId, otherStage.Id), (otherPipeline.Id, localStage.Id),
            (Guid.NewGuid(), localStage.Id), (pipelineId, Guid.NewGuid())
        })
        {
            var route = $"/api/pipelines/{pair.Item1}/stages/{pair.Item2}";
            using var get = await client.GetAsync(route);
            using var update = await client.PutAsJsonAsync(route, new { Name = "Rejected", CategoryCode = "failure" });
            Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, update.StatusCode);
            foreach (var action in new[] { "archive", "restore" })
            {
                using var response = await client.PostAsync($"{route}/{action}", null);
                Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            }
        }
        Assert.Equal(localStage.Id, Assert.Single((await client.GetFromJsonAsync<List<PipelineStageDto>>(Route))!).Id);
        Assert.Equal(3, await db.PipelineStages.CountAsync());
        Assert.All(await db.PipelineStages.AsNoTracking().ToListAsync(), x =>
        {
            Assert.Null(x.UpdatedAt);
            Assert.Null(x.ArchivedAt);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OpportunityRulesStillRejectNewAssignmentsToArchivedStageOrParent(bool archiveParent)
    {
        var target = await CreateAsync("Target");
        var source = await CreateAsync("Source");
        var existing = await client.PostAsJsonAsync("/api/opportunities", new CreateOpportunityRequest
        {
            Title = "Existing", PipelineStageId = target.Id, PriorityCode = "normal"
        });
        Assert.Equal(HttpStatusCode.Created, existing.StatusCode);
        var existingId = (await existing.Content.ReadFromJsonAsync<OpportunityDto>())!.Id;
        existing.Dispose();
        using var sourceResponse = await client.PostAsJsonAsync("/api/opportunities", new CreateOpportunityRequest
        {
            Title = "Other", PipelineStageId = source.Id, PriorityCode = "normal"
        });
        Assert.Equal(HttpStatusCode.Created, sourceResponse.StatusCode);
        var sourceId = (await sourceResponse.Content.ReadFromJsonAsync<OpportunityDto>())!.Id;
        if (archiveParent)
        {
            using var response = await client.PostAsync($"/api/pipelines/{pipelineId}/archive", null);
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }
        else
            await ActionAsync(target.Id, "archive");
        using var create = await client.PostAsJsonAsync("/api/opportunities", new CreateOpportunityRequest
        {
            Title = "Rejected", PipelineStageId = target.Id, PriorityCode = "normal"
        });
        Assert.Equal(HttpStatusCode.BadRequest, create.StatusCode);
        using var reassignment = await client.PutAsJsonAsync($"/api/opportunities/{sourceId}", new UpdateOpportunityRequest
        {
            Title = "Rejected", PipelineStageId = target.Id, PriorityCode = "normal"
        });
        Assert.Equal(HttpStatusCode.BadRequest, reassignment.StatusCode);
        using var history = await client.PutAsJsonAsync($"/api/opportunities/{existingId}", new UpdateOpportunityRequest
        {
            Title = "History still editable", PipelineStageId = target.Id, PriorityCode = "normal"
        });
        Assert.Equal(HttpStatusCode.NoContent, history.StatusCode);
        await using var db = CreateContext();
        Assert.Equal(2, await db.Opportunities.CountAsync());
        Assert.Equal(source.Id, (await db.Opportunities.SingleAsync(x => x.Id == sourceId)).PipelineStageId);
    }

    [Fact]
    public async Task ConcurrentCreatesWaitAndAppendDistinctPositions()
    {
        await CreateAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var token = timeout.Token;
        await using var blocker = CreateContext();
        await using var transaction = await blocker.Database.BeginTransactionAsync(token);
        await blocker.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"Pipelines\" WHERE \"Id\" = {pipelineId} FOR UPDATE", token);
        await using var firstDb = CreateContext();
        await using var secondDb = CreateContext();
        var first = new PipelineStageService(firstDb, new CurrentWorkspaceProvider(firstDb)).CreateAsync(pipelineId,
            new() { Name = "First concurrent", CategoryCode = "active" }, token);
        var second = new PipelineStageService(secondDb, new CurrentWorkspaceProvider(secondDb)).CreateAsync(pipelineId,
            new() { Name = "Second concurrent", CategoryCode = "success" }, token);
        await WaitForBlockedWritesAsync(2, token);
        Assert.False(first.IsCompleted);
        Assert.False(second.IsCompleted);
        await transaction.CommitAsync(token);
        var results = await Task.WhenAll(first, second);
        Assert.All(results, x => Assert.Equal(PipelineStageWriteStatus.Succeeded, x.Status));
        Assert.Equal(new[] { 1, 2 }, results.Select(x => x.Stage!.SortOrder).Order());
        await using var check = CreateContext();
        Assert.Equal(new[] { 0, 1, 2 }, await check.PipelineStages.OrderBy(x => x.SortOrder).Select(x => x.SortOrder).ToArrayAsync(token));
    }

    [Fact]
    public async Task LockedPipelineDoesNotBlockCreationInAnotherPipeline()
    {
        await using var setup = CreateContext();
        var other = new Pipeline { WorkspaceId = workspaceId, Name = "Independent", TypeCode = "custom" };
        setup.Pipelines.Add(other);
        await setup.SaveChangesAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var token = timeout.Token;
        await using var blocker = CreateContext();
        await using var transaction = await blocker.Database.BeginTransactionAsync(token);
        await blocker.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT 1 FROM \"Pipelines\" WHERE \"Id\" = {pipelineId} FOR UPDATE", token);
        await using var waitingDb = CreateContext();
        var waiting = new PipelineStageService(waitingDb, new CurrentWorkspaceProvider(waitingDb)).CreateAsync(
            pipelineId, new() { Name = "Waiting", CategoryCode = "active" }, token);
        await WaitForBlockedWritesAsync(1, token);
        await using var independentDb = CreateContext();
        var independent = await new PipelineStageService(independentDb, new CurrentWorkspaceProvider(independentDb))
            .CreateAsync(other.Id, new() { Name = "Independent", CategoryCode = "success" }, token);
        Assert.Equal(PipelineStageWriteStatus.Succeeded, independent.Status);
        Assert.Equal(other.Id, independent.Stage!.PipelineId);
        Assert.Equal(0, independent.Stage.SortOrder);
        Assert.False(waiting.IsCompleted);
        await transaction.CommitAsync(token);
        var released = await waiting;
        Assert.Equal(PipelineStageWriteStatus.Succeeded, released.Status);
        Assert.Equal(pipelineId, released.Stage!.PipelineId);
        Assert.Equal(0, released.Stage.SortOrder);
    }

    [Fact]
    public async Task WaitingCreateRechecksParentAfterConcurrentArchiveCommits()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var token = timeout.Token;
        await using var blocker = CreateContext();
        await using var transaction = await blocker.Database.BeginTransactionAsync(token);
        // Hold the same row lock taken by PipelineService's archive UPDATE before its commit.
        await blocker.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE \"Pipelines\" SET \"ArchivedAt\" = {DateTimeOffset.UtcNow} WHERE \"Id\" = {pipelineId}", token);
        await using var writer = CreateContext();
        var create = new PipelineStageService(writer, new CurrentWorkspaceProvider(writer)).CreateAsync(pipelineId,
            new() { Name = "Rejected", CategoryCode = "active" }, token);
        await WaitForBlockedWritesAsync(1, token);
        Assert.False(create.IsCompleted);
        await transaction.CommitAsync(token);
        Assert.Equal(PipelineStageWriteStatus.Conflict, (await create).Status);
        Assert.Empty(await writer.PipelineStages.AsNoTracking().ToListAsync(token));
    }

    [Fact]
    public async Task ExhaustedSortOrderReturns409WithoutOverflowOrRenumbering()
    {
        await using var db = CreateContext();
        db.PipelineStages.Add(new PipelineStage
        {
            PipelineId = pipelineId, Name = "Last", CategoryCode = "active", SortOrder = int.MaxValue, ArchivedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
        using var response = await client.PostAsJsonAsync(Route, new { Name = "Rejected", CategoryCode = "active" });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(int.MaxValue, (await db.PipelineStages.AsNoTracking().SingleAsync()).SortOrder);
    }

    private async Task WaitForBlockedWritesAsync(int count, CancellationToken token)
    {
        await using var observer = new NpgsqlConnection(postgres.GetConnectionString());
        await observer.OpenAsync(token);
        await using var waiting = new NpgsqlCommand(
            "SELECT count(*) FROM pg_stat_activity WHERE datname = current_database() AND wait_event_type = 'Lock' AND query LIKE '%Pipelines%'", observer);
        while (Convert.ToInt64(await waiting.ExecuteScalarAsync(token)) < count)
            await Task.Delay(25, token);
    }

    private static async Task<string> OpportunitySnapshotAsync(ProspectionCrmDbContext db)
        => JsonSerializer.Serialize(await db.Opportunities.AsNoTracking().OrderBy(x => x.Id).ToListAsync());
}
