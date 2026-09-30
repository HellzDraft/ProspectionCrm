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

public sealed class PipelineStageOrderTests : IAsyncLifetime
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
        factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder
            .UseEnvironment("Testing").UseSetting("ConnectionStrings:DefaultConnection", postgres.GetConnectionString()));
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

    private static PipelineStageService Service(ProspectionCrmDbContext db) => new(db, new CurrentWorkspaceProvider(db));

    private async Task<PipelineStage[]> SeedAsync(params int[] positions)
    {
        await using var db = CreateContext();
        var stages = positions.Select((position, index) => new PipelineStage
        {
            PipelineId = pipelineId, Name = $"Stage {index}", Description = $"Description {index}",
            CategoryCode = "active", SortOrder = position
        }).ToArray();
        db.PipelineStages.AddRange(stages);
        await db.SaveChangesAsync();
        return stages;
    }

    private async Task OrderAsync(Guid[] ids, HttpStatusCode expected = HttpStatusCode.NoContent, Guid? parent = null)
    {
        using var response = await client.PutAsJsonAsync($"/api/pipelines/{parent ?? pipelineId}/stages/order", new { stageIds = ids });
        Assert.Equal(expected, response.StatusCode);
        if (expected == HttpStatusCode.NoContent)
            Assert.Empty(await response.Content.ReadAsStringAsync());
    }

    private async Task<PipelineStageDto[]> ReadAsync() =>
        (await client.GetFromJsonAsync<PipelineStageDto[]>($"{Route}?includeArchived=true"))!;

    private async Task<string> SnapshotAsync()
    {
        await using var db = CreateContext();
        return JsonSerializer.Serialize(new
        {
            Pipelines = await db.Pipelines.AsNoTracking().OrderBy(x => x.Id).ToArrayAsync(),
            Stages = await db.PipelineStages.AsNoTracking().OrderBy(x => x.Id).ToArrayAsync(),
            Opportunities = await db.Opportunities.AsNoTracking().OrderBy(x => x.Id).ToArrayAsync()
        });
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    public async Task PermutationsPreserveSlotsAndRepeatedOrderIsANoop(int count)
    {
        var stages = await SeedAsync(Enumerable.Range(0, count).Select(x => x * 3).ToArray());
        var before = await ReadAsync();
        var ids = stages.Reverse().Select(x => x.Id).ToArray();
        await OrderAsync(ids);
        var after = await ReadAsync();
        Assert.Equal(ids, after.Select(x => x.Id));
        Assert.Equal(before.Select(x => x.SortOrder), after.Select(x => x.SortOrder));
        foreach (var stage in after)
        {
            var original = before.Single(x => x.Id == stage.Id);
            Assert.NotNull(stage.UpdatedAt);
            original.SortOrder = stage.SortOrder;
            original.UpdatedAt = stage.UpdatedAt;
            Assert.Equal(JsonSerializer.Serialize(original), JsonSerializer.Serialize(stage));
        }
        var snapshot = await SnapshotAsync();
        await OrderAsync(ids);
        Assert.Equal(snapshot, await SnapshotAsync());
    }

    [Fact]
    public async Task ArchivedSlotsAndGapsStayFixedAndLifecycleStillWorks()
    {
        var stages = await SeedAsync(0, 1, 2, 7, 10);
        using var archive = await client.PostAsync($"{Route}/{stages[1].Id}/archive", null);
        Assert.Equal(HttpStatusCode.NoContent, archive.StatusCode);
        var before = await ReadAsync();
        await OrderAsync([stages[0].Id, stages[4].Id, stages[3].Id, stages[2].Id]);
        var after = await ReadAsync();
        Assert.Equal(new[] { 0, 1, 2, 7, 10 }, after.Select(x => x.SortOrder));
        Assert.Equal(new[] { stages[0].Id, stages[1].Id, stages[4].Id, stages[3].Id, stages[2].Id }, after.Select(x => x.Id));
        foreach (var index in new[] { 0, 1, 3 })
            Assert.Equal(JsonSerializer.Serialize(before[index]), JsonSerializer.Serialize(after[index]));
        using var restore = await client.PostAsync($"{Route}/{stages[1].Id}/restore", null);
        Assert.Equal(HttpStatusCode.NoContent, restore.StatusCode);
        Assert.Equal(1, (await ReadAsync()).Single(x => x.Id == stages[1].Id).SortOrder);
        using var create = await client.PostAsJsonAsync(Route, new { Name = "Appended", CategoryCode = "active" });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        Assert.Equal(11, (await create.Content.ReadFromJsonAsync<PipelineStageDto>())!.SortOrder);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EmptyOrderSucceedsOnlyWithoutActiveStages(bool archivedOnly)
    {
        if (archivedOnly)
        {
            var stages = await SeedAsync(9);
            using var archive = await client.PostAsync($"{Route}/{stages[0].Id}/archive", null);
            Assert.Equal(HttpStatusCode.NoContent, archive.StatusCode);
        }
        var before = await SnapshotAsync();
        await OrderAsync([]);
        Assert.Equal(before, await SnapshotAsync());
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("duplicate")]
    [InlineData("unknown")]
    [InlineData("other-pipeline")]
    [InlineData("other-workspace")]
    [InlineData("archived")]
    [InlineData("missing")]
    [InlineData("extra")]
    public async Task InvalidPermutationReturns400WithoutAnyMutation(string kind)
    {
        var stages = await SeedAsync(0, 1, 2);
        await using var db = CreateContext();
        var archived = await db.PipelineStages.SingleAsync(x => x.Id == stages[2].Id);
        archived.ArchivedAt = DateTimeOffset.UtcNow;
        var other = new Pipeline { WorkspaceId = workspaceId, Name = "Other", TypeCode = "custom" };
        var foreignWorkspace = new Workspace
        {
            OwnerUserId = (await db.UserAccounts.SingleAsync()).Id, Name = "Foreign", TimeZoneId = "UTC", ArchivedAt = DateTimeOffset.UtcNow
        };
        var foreign = new Pipeline { Workspace = foreignWorkspace, Name = "Foreign", TypeCode = "custom" };
        var otherStage = new PipelineStage { Pipeline = other, Name = "Other", CategoryCode = "active" };
        var foreignStage = new PipelineStage { Pipeline = foreign, Name = "Foreign", CategoryCode = "active" };
        db.PipelineStages.AddRange(otherStage, foreignStage);
        await db.SaveChangesAsync();
        Guid[] ids = kind switch
        {
            "empty" => [],
            "duplicate" => [stages[0].Id, stages[0].Id],
            "unknown" => [stages[0].Id, Guid.NewGuid()],
            "other-pipeline" => [stages[0].Id, otherStage.Id],
            "other-workspace" => [stages[0].Id, foreignStage.Id],
            "archived" => [stages[0].Id, stages[2].Id],
            "missing" => [stages[0].Id],
            _ => [stages[0].Id, stages[1].Id, Guid.NewGuid()]
        };
        var before = await SnapshotAsync();
        await OrderAsync(ids, HttpStatusCode.BadRequest);
        Assert.Equal(before, await SnapshotAsync());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"stageIds\":null}")]
    [InlineData("{\"stageIds\":[\"not-a-guid\"]}")]
    public async Task MissingNullOrMalformedIdsReturn400(string body)
    {
        using var response = await client.PutAsync($"{Route}/order", new StringContent(body, System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task MissingForeignAndArchivedParentsRejectReorder()
    {
        var stages = await SeedAsync(0, 1);
        await using var db = CreateContext();
        var foreign = new Pipeline
        {
            Workspace = new Workspace
            {
                OwnerUserId = (await db.UserAccounts.SingleAsync()).Id, Name = "Foreign", TimeZoneId = "UTC", ArchivedAt = DateTimeOffset.UtcNow
            },
            Name = "Foreign", TypeCode = "custom"
        };
        db.Pipelines.Add(foreign);
        await db.SaveChangesAsync();
        var before = await SnapshotAsync();
        await OrderAsync([], HttpStatusCode.NotFound, Guid.NewGuid());
        await OrderAsync([], HttpStatusCode.NotFound, foreign.Id);
        Assert.Equal(before, await SnapshotAsync());
        using var archive = await client.PostAsync($"/api/pipelines/{pipelineId}/archive", null);
        Assert.Equal(HttpStatusCode.NoContent, archive.StatusCode);
        before = await SnapshotAsync();
        await OrderAsync(stages.Reverse().Select(x => x.Id).ToArray(), HttpStatusCode.Conflict);
        Assert.Equal(before, await SnapshotAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IntMaxValueInActiveOrArchivedSlotsDoesNotOverflow(bool archiveMaximum)
    {
        var stages = await SeedAsync(0, 2, int.MaxValue);
        if (archiveMaximum)
        {
            using var archive = await client.PostAsync($"{Route}/{stages[2].Id}/archive", null);
            Assert.Equal(HttpStatusCode.NoContent, archive.StatusCode);
        }
        var ids = stages.Take(archiveMaximum ? 2 : 3).Reverse().Select(x => x.Id).ToArray();
        await OrderAsync(ids);
        var after = await ReadAsync();
        Assert.Equal(new[] { 0, 2, int.MaxValue }, after.Select(x => x.SortOrder));
        Assert.Equal(ids, after.Where(x => x.ArchivedAt is null).Select(x => x.Id));
        if (archiveMaximum)
            Assert.Equal(int.MaxValue, after.Single(x => x.Id == stages[2].Id).SortOrder);
    }

    [Fact]
    public async Task FailureDuringFinalSaveRollsBackTemporaryPositionsAndTimestamps()
    {
        var stages = await SeedAsync(0, 1);
        var before = await SnapshotAsync();
        await using var db = CreateContext();
        // Test-only constraint accepts original and temporary positions, rejects A's final position.
        await db.Database.ExecuteSqlRawAsync(
            "ALTER TABLE \"PipelineStages\" ADD CONSTRAINT reject_final CHECK (\"Name\" <> 'Stage 0' OR \"SortOrder\" <> 1)");
        await Assert.ThrowsAsync<DbUpdateException>(() => Service(db).ReorderAsync(pipelineId,
            new() { StageIds = [stages[1].Id, stages[0].Id] }, default));
        Assert.Equal(before, await SnapshotAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OpportunitiesAreUnchangedAndArchivedAssignmentsStayForbidden(bool archiveParent)
    {
        var stages = await SeedAsync(0, 1, 2);
        await using (var db = CreateContext())
        {
            db.Opportunities.AddRange(stages.Select((stage, i) => new Opportunity
            {
                WorkspaceId = workspaceId, PipelineStageId = stage.Id, Title = $"History {i}", PriorityCode = "high",
                Score = 75, ScoredAt = DateTimeOffset.UtcNow, Location = "Paris", Notes = "Preserve all fields",
                ArchivedAt = i == 2 ? DateTimeOffset.UtcNow : null
            }));
            await db.SaveChangesAsync();
        }
        using var archive = await client.PostAsync($"{Route}/{stages[2].Id}/archive", null);
        Assert.Equal(HttpStatusCode.NoContent, archive.StatusCode);
        await using var check = CreateContext();
        var before = JsonSerializer.Serialize(await check.Opportunities.AsNoTracking().OrderBy(x => x.Id).ToArrayAsync());
        await OrderAsync([stages[1].Id, stages[0].Id]);
        Assert.Equal(before, JsonSerializer.Serialize(await check.Opportunities.AsNoTracking().OrderBy(x => x.Id).ToArrayAsync()));
        if (archiveParent)
        {
            using var parentArchive = await client.PostAsync($"/api/pipelines/{pipelineId}/archive", null);
            Assert.Equal(HttpStatusCode.NoContent, parentArchive.StatusCode);
        }
        var target = stages[archiveParent ? 1 : 2].Id;
        using var create = await client.PostAsJsonAsync("/api/opportunities", new CreateOpportunityRequest
        {
            Title = "Rejected", PipelineStageId = target, PriorityCode = "normal"
        });
        Assert.Equal(HttpStatusCode.BadRequest, create.StatusCode);
        var source = await check.Opportunities.AsNoTracking().SingleAsync(x => x.PipelineStageId == stages[0].Id);
        using var update = await client.PutAsJsonAsync($"/api/opportunities/{source.Id}", new UpdateOpportunityRequest
        {
            Title = "Rejected", PipelineStageId = target, PriorityCode = "normal"
        });
        Assert.Equal(HttpStatusCode.BadRequest, update.StatusCode);
        Assert.Equal(before, JsonSerializer.Serialize(await check.Opportunities.AsNoTracking().OrderBy(x => x.Id).ToArrayAsync()));
    }

    [Fact]
    public async Task ConcurrentReordersSerializeWithoutMixingPermutations()
    {
        var stages = await SeedAsync(0, 1, 4);
        Guid[] firstOrder = [stages[2].Id, stages[0].Id, stages[1].Id];
        Guid[] secondOrder = [stages[1].Id, stages[2].Id, stages[0].Id];
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        await using var blocker = CreateContext();
        await using var transaction = await blocker.Database.BeginTransactionAsync(token);
        await LockAsync(blocker, token);
        await using var firstDb = CreateContext();
        await using var secondDb = CreateContext();
        var first = Service(firstDb).ReorderAsync(pipelineId, new() { StageIds = firstOrder }, token);
        await WaitForBlockedWritesAsync(1, token);
        var second = Service(secondDb).ReorderAsync(pipelineId, new() { StageIds = secondOrder }, token);
        await WaitForBlockedWritesAsync(2, token);
        Assert.False(first.IsCompleted);
        Assert.False(second.IsCompleted);
        await transaction.CommitAsync(token);
        Assert.All(await Task.WhenAll(first, second), x => Assert.Equal(PipelineStageWriteStatus.Succeeded, x.Status));
        var after = await ReadAsync();
        Assert.Equal(new[] { 0, 1, 4 }, after.Select(x => x.SortOrder));
        var actual = after.Select(x => x.Id).ToArray();
        Assert.True(actual.SequenceEqual(firstOrder) || actual.SequenceEqual(secondOrder));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentCreateAndReorderUseCommittedStageSet(bool createFirst)
    {
        var stages = await SeedAsync(0, 2);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        await using var blocker = CreateContext();
        await using var transaction = await blocker.Database.BeginTransactionAsync(token);
        await LockAsync(blocker, token);
        await using var orderDb = CreateContext();
        await using var createDb = CreateContext();
        Task<PipelineStageWriteResult> Create() => Service(createDb).CreateAsync(pipelineId, new() { Name = "New", CategoryCode = "active" }, token);
        Task<PipelineStageWriteResult> Reorder() => Service(orderDb).ReorderAsync(pipelineId, new() { StageIds = [stages[1].Id, stages[0].Id] }, token);
        var first = createFirst ? Create() : Reorder();
        await WaitForBlockedWritesAsync(1, token);
        var second = createFirst ? Reorder() : Create();
        await WaitForBlockedWritesAsync(2, token);
        Assert.False(first.IsCompleted);
        Assert.False(second.IsCompleted);
        await transaction.CommitAsync(token);
        await Task.WhenAll(first, second);
        var created = await (createFirst ? first : second);
        var ordered = await (createFirst ? second : first);
        Assert.Equal(PipelineStageWriteStatus.Succeeded, created.Status);
        Assert.Equal(3, created.Stage!.SortOrder);
        Assert.Contains(ordered.Status, new[] { PipelineStageWriteStatus.Succeeded, PipelineStageWriteStatus.InvalidInput });
        var after = await ReadAsync();
        Assert.Equal(new[] { 0, 2, 3 }, after.Select(x => x.SortOrder));
        Assert.Equal(ordered.Status == PipelineStageWriteStatus.Succeeded
            ? new[] { stages[1].Id, stages[0].Id, created.Stage.Id }
            : new[] { stages[0].Id, stages[1].Id, created.Stage.Id }, after.Select(x => x.Id));
    }

    [Theory]
    [InlineData("create")]
    [InlineData("archive-stage")]
    [InlineData("archive-parent")]
    public async Task WaitingReorderValidatesStateAfterLockAcquisition(string change)
    {
        var stages = await SeedAsync(0, 1);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        await using var blocker = CreateContext();
        await using var transaction = await blocker.Database.BeginTransactionAsync(token);
        await LockAsync(blocker, token);
        await using var writer = CreateContext();
        var reorder = Service(writer).ReorderAsync(pipelineId, new() { StageIds = [stages[1].Id, stages[0].Id] }, token);
        await WaitForBlockedWritesAsync(1, token);
        if (change == "create")
            blocker.PipelineStages.Add(new PipelineStage { PipelineId = pipelineId, Name = "New", CategoryCode = "active", SortOrder = 2 });
        else if (change == "archive-stage")
            (await blocker.PipelineStages.SingleAsync(x => x.Id == stages[0].Id, token)).ArchivedAt = DateTimeOffset.UtcNow;
        else
            (await blocker.Pipelines.SingleAsync(x => x.Id == pipelineId, token)).ArchivedAt = DateTimeOffset.UtcNow;
        await blocker.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        Assert.Equal(change == "archive-parent" ? PipelineStageWriteStatus.Conflict : PipelineStageWriteStatus.InvalidInput,
            (await reorder).Status);
        var after = await ReadAsync();
        Assert.Equal(0, after.Single(x => x.Id == stages[0].Id).SortOrder);
        Assert.Equal(1, after.Single(x => x.Id == stages[1].Id).SortOrder);
        Assert.All(after, x => Assert.Null(x.UpdatedAt));
    }

    [Fact]
    public async Task ReorderingAnotherPipelineDoesNotWaitForLockedParent()
    {
        var stages = await SeedAsync(0, 1);
        await using var setup = CreateContext();
        var other = new Pipeline { WorkspaceId = workspaceId, Name = "Other", TypeCode = "custom" };
        var otherStages = new[]
        {
            new PipelineStage { Pipeline = other, Name = "A", CategoryCode = "active", SortOrder = 0 },
            new PipelineStage { Pipeline = other, Name = "B", CategoryCode = "active", SortOrder = 1 }
        };
        setup.PipelineStages.AddRange(otherStages);
        await setup.SaveChangesAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        await using var blocker = CreateContext();
        await using var transaction = await blocker.Database.BeginTransactionAsync(token);
        await LockAsync(blocker, token);
        await using var waitingDb = CreateContext();
        var waiting = Service(waitingDb).ReorderAsync(pipelineId, new() { StageIds = [stages[1].Id, stages[0].Id] }, token);
        await WaitForBlockedWritesAsync(1, token);
        await using var otherDb = CreateContext();
        var independent = await Service(otherDb).ReorderAsync(other.Id,
            new() { StageIds = [otherStages[1].Id, otherStages[0].Id] }, token);
        Assert.Equal(PipelineStageWriteStatus.Succeeded, independent.Status);
        Assert.False(waiting.IsCompleted);
        Assert.Equal(new[] { otherStages[1].Id, otherStages[0].Id }, await otherDb.PipelineStages.AsNoTracking()
            .Where(x => x.PipelineId == other.Id).OrderBy(x => x.SortOrder).Select(x => x.Id).ToArrayAsync(token));
        await transaction.CommitAsync(token);
        Assert.Equal(PipelineStageWriteStatus.Succeeded, (await waiting).Status);
    }

    private Task<int> LockAsync(ProspectionCrmDbContext db, CancellationToken token) => db.Database.ExecuteSqlInterpolatedAsync(
        $"SELECT 1 FROM \"Pipelines\" WHERE \"Id\" = {pipelineId} FOR UPDATE", token);

    private async Task WaitForBlockedWritesAsync(int count, CancellationToken token)
    {
        await using var observer = new NpgsqlConnection(postgres.GetConnectionString());
        await observer.OpenAsync(token);
        await using var waiting = new NpgsqlCommand(
            "SELECT count(*) FROM pg_stat_activity WHERE datname = current_database() AND wait_event_type = 'Lock' AND query LIKE '%Pipelines%'", observer);
        while (Convert.ToInt64(await waiting.ExecuteScalarAsync(token)) < count)
            await Task.Delay(25, token);
    }
}
