using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using ProspectionCrm.Api.Data;
using ProspectionCrm.Api.Dtos.Pipelines;
using ProspectionCrm.Api.Dtos.Opportunities;
using ProspectionCrm.Api.Entities;
using ProspectionCrm.Api.Services;
using Testcontainers.PostgreSql;
using Xunit;

namespace ProspectionCrm.Api.Tests;

// Same isolation as BootstrapTests: only the disposable container's connection is used.
public sealed class PipelineTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer postgres = new PostgreSqlBuilder("postgres:18").Build();
    private WebApplicationFactory<Program> factory = null!;
    private HttpClient client = null!;
    private Guid workspaceId;

    public async Task InitializeAsync()
    {
        await postgres.StartAsync();
        await using var db = CreateContext();
        await db.Database.MigrateAsync();
        var setup = await new BootstrapService(db).BootstrapAsync(default);
        workspaceId = setup.Bootstrap!.WorkspaceId;
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

    private async Task<PipelineDto> CreateAsync(string name = "Pipeline", string type = "custom", bool visible = true)
    {
        using var response = await client.PostAsJsonAsync("/api/pipelines",
            new PipelineWriteRequest { Name = name, TypeCode = type, IsVisible = visible });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var pipeline = await response.Content.ReadFromJsonAsync<PipelineDto>();
        Assert.NotNull(pipeline);
        Assert.EndsWith($"/api/pipelines/{pipeline.Id}", response.Headers.Location?.ToString());
        return pipeline;
    }

    private async Task<PipelineDto> GetAsync(Guid id, bool includeArchivedStages = false)
        => (await client.GetFromJsonAsync<PipelineDto>(includeArchivedStages
            ? $"/api/pipelines/{id}?includeArchivedStages=true"
            : $"/api/pipelines/{id}"))!;

    private async Task ActionAsync(Guid id, string action, HttpStatusCode status = HttpStatusCode.NoContent)
    {
        using var response = await client.PostAsync($"/api/pipelines/{id}/{action}", null);
        Assert.Equal(status, response.StatusCode);
    }

    [Theory]
    [InlineData("employment")]
    [InlineData("freelance")]
    [InlineData("business")]
    [InlineData("custom")]
    public async Task CreateAcceptsEachTypeAndCreatesOnlyPipeline(string type)
    {
        // Anonymous body ensures trimming is performed by the API, not the test's request setter.
        using var response = await client.PostAsJsonAsync("/api/pipelines", new
        {
            Name = "  Same name  ", TypeCode = type, WorkspaceId = Guid.NewGuid(),
            DefaultPipelineId = Guid.NewGuid(), ArchivedAt = DateTimeOffset.UtcNow
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var pipeline = (await response.Content.ReadFromJsonAsync<PipelineDto>())!;
        Assert.Equal("Same name", pipeline.Name);
        Assert.Equal(type, pipeline.TypeCode);
        Assert.True(pipeline.IsVisible);
        Assert.False(pipeline.IsDefault);
        Assert.Null(pipeline.ArchivedAt);
        Assert.Empty(pipeline.Stages);
        var duplicate = await CreateAsync("Same name", type, visible: false);
        Assert.NotEqual(pipeline.Id, duplicate.Id);
        Assert.False(duplicate.IsVisible);
        var list = (await client.GetFromJsonAsync<List<PipelineDto>>("/api/pipelines"))!;
        Assert.Equal(2, list.Count);
        Assert.Contains(list, x => x.Id == duplicate.Id && !x.IsVisible);
        Assert.Equal(pipeline.Name, (await GetAsync(pipeline.Id)).Name);
        await using var db = CreateContext();
        Assert.All(await db.Pipelines.ToListAsync(), x => Assert.Equal(workspaceId, x.WorkspaceId));
        Assert.Null((await db.Workspaces.SingleAsync()).DefaultPipelineId);
        Assert.Empty(await db.PipelineStages.ToListAsync());
        Assert.Empty(await db.Opportunities.ToListAsync());
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("blank")]
    [InlineData("null-name")]
    [InlineData("long-name")]
    [InlineData("long-description")]
    [InlineData("invalid-type")]
    [InlineData("null-type")]
    [InlineData("wrong-case-type")]
    public async Task CreateAndUpdateRejectInvalidInputWithoutWriting(string invalid)
    {
        var pipeline = await CreateAsync();
        var body = new
        {
            Name = invalid switch { "empty" => "", "blank" => " \t ", "null-name" => null, "long-name" => new string('a', 201), _ => "Valid" },
            Description = invalid == "long-description" ? new string('d', 2001) : null,
            TypeCode = invalid switch { "invalid-type" => "unknown", "null-type" => null, "wrong-case-type" => "Custom", _ => "custom" }
        };
        using var create = await client.PostAsJsonAsync("/api/pipelines", body);
        using var update = await client.PutAsJsonAsync($"/api/pipelines/{pipeline.Id}", body);
        Assert.Equal(HttpStatusCode.BadRequest, create.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, update.StatusCode);
        await using var db = CreateContext();
        var saved = Assert.Single(await db.Pipelines.ToListAsync());
        Assert.Equal("Pipeline", saved.Name);
        Assert.Null(saved.UpdatedAt);
    }

    [Fact]
    public async Task UpdateAcceptsBoundaryLengthsAndCannotChangeProtectedFields()
    {
        var pipeline = await CreateAsync();
        using var response = await client.PutAsJsonAsync($"/api/pipelines/{pipeline.Id}", new
        {
            Name = "  " + new string('n', 200) + "  ", Description = new string('d', 2000), TypeCode = "business", IsVisible = false,
            WorkspaceId = Guid.NewGuid(), DefaultPipelineId = pipeline.Id, ArchivedAt = DateTimeOffset.UtcNow,
            PreferredCandidateProfileId = Guid.NewGuid(), Stages = new[] { new { Name = "Not created" } }
        });
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var updated = await GetAsync(pipeline.Id);
        Assert.Equal(new string('n', 200), updated.Name);
        Assert.Equal(new string('d', 2000), updated.Description);
        Assert.Equal("business", updated.TypeCode);
        Assert.False(updated.IsVisible);
        Assert.False(updated.IsDefault);
        Assert.Null(updated.ArchivedAt);
        Assert.Null(updated.PreferredCandidateProfileId);
        Assert.NotNull(updated.UpdatedAt);
        Assert.Empty(updated.Stages);
        await using var db = CreateContext();
        Assert.Equal(workspaceId, (await db.Pipelines.SingleAsync()).WorkspaceId);
    }

    [Fact]
    public async Task EveryRouteTreatsForeignAndMissingPipelinesAsNotFound()
    {
        await using var db = CreateContext();
        // Keep exactly one active workspace, without bypassing CurrentWorkspaceProvider.
        var other = new Workspace
        {
            OwnerUserId = (await db.UserAccounts.SingleAsync()).Id, Name = "Other", TimeZoneId = "UTC", ArchivedAt = DateTimeOffset.UtcNow
        };
        var foreign = new Pipeline { Workspace = other, Name = "Foreign", TypeCode = "custom" };
        db.Pipelines.Add(foreign);
        await db.SaveChangesAsync();
        foreach (var id in new[] { foreign.Id, Guid.NewGuid() })
        {
            using var get = await client.GetAsync($"/api/pipelines/{id}?includeArchivedStages=true");
            Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
            using var put = await client.PutAsJsonAsync($"/api/pipelines/{id}", new PipelineWriteRequest { Name = "Changed", TypeCode = "custom" });
            Assert.Equal(HttpStatusCode.NotFound, put.StatusCode);
            foreach (var action in new[] { "archive", "restore", "set-default" })
                await ActionAsync(id, action, HttpStatusCode.NotFound);
        }
        Assert.Empty((await client.GetFromJsonAsync<List<PipelineDto>>("/api/pipelines?includeArchived=true"))!);
        var unchanged = await db.Pipelines.AsNoTracking().SingleAsync();
        Assert.Equal("Foreign", unchanged.Name);
        Assert.Null(unchanged.ArchivedAt);
        Assert.Null(unchanged.UpdatedAt);
        Assert.All(await db.Workspaces.AsNoTracking().ToListAsync(), x => Assert.Null(x.DefaultPipelineId));
    }

    [Fact]
    public async Task DefaultReplacementArchiveRestoreAndVisibilityAreIndependent()
    {
        var first = await CreateAsync();
        var second = await CreateAsync("Second", visible: false);
        await ActionAsync(first.Id, "set-default");
        Assert.True((await GetAsync(first.Id)).IsDefault);
        await ActionAsync(second.Id, "set-default");
        Assert.False((await GetAsync(first.Id)).IsDefault);
        Assert.True((await GetAsync(second.Id)).IsDefault);
        var list = (await client.GetFromJsonAsync<List<PipelineDto>>("/api/pipelines"))!;
        Assert.Equal(second.Id, Assert.Single(list, x => x.IsDefault).Id);
        await ActionAsync(first.Id, "archive");
        Assert.True((await GetAsync(second.Id)).IsDefault);
        await ActionAsync(second.Id, "archive");
        var archived = await GetAsync(second.Id);
        Assert.NotNull(archived.ArchivedAt);
        Assert.False(archived.IsVisible);
        Assert.False(archived.IsDefault);
        await ActionAsync(second.Id, "archive");
        Assert.Equal(archived.ArchivedAt, (await GetAsync(second.Id)).ArchivedAt);
        Assert.Empty((await client.GetFromJsonAsync<List<PipelineDto>>("/api/pipelines"))!);
        Assert.Equal(2, (await client.GetFromJsonAsync<List<PipelineDto>>("/api/pipelines?includeArchived=true"))!.Count);
        await ActionAsync(second.Id, "set-default", HttpStatusCode.Conflict);
        using var update = await client.PutAsJsonAsync($"/api/pipelines/{second.Id}",
            new PipelineWriteRequest { Name = "Visible but archived", TypeCode = "freelance", IsVisible = true });
        Assert.Equal(HttpStatusCode.NoContent, update.StatusCode);
        var stillArchived = await GetAsync(second.Id);
        Assert.True(stillArchived.IsVisible);
        Assert.Equal(archived.ArchivedAt, stillArchived.ArchivedAt);
        await ActionAsync(second.Id, "restore");
        await ActionAsync(second.Id, "restore");
        var restored = await GetAsync(second.Id);
        Assert.Null(restored.ArchivedAt);
        Assert.False(restored.IsDefault);
        Assert.True(restored.IsVisible);
        await using var db = CreateContext();
        Assert.Null((await db.Workspaces.SingleAsync()).DefaultPipelineId);
        using var delete = await client.DeleteAsync($"/api/pipelines/{second.Id}");
        Assert.Equal(HttpStatusCode.MethodNotAllowed, delete.StatusCode);
        Assert.Equal(2, await db.Pipelines.CountAsync());
    }

    [Fact]
    public async Task ArchivePreservesChildrenAndOpportunityAssignmentRules()
    {
        var pipeline = await CreateAsync();
        await using var db = CreateContext();
        var stage = new PipelineStage { PipelineId = pipeline.Id, Name = "Active", CategoryCode = "active", SortOrder = 2 };
        var archivedStage = new PipelineStage { PipelineId = pipeline.Id, Name = "Archived", CategoryCode = "failure", SortOrder = 1, ArchivedAt = DateTimeOffset.UtcNow };
        var opportunity = new Opportunity { WorkspaceId = workspaceId, PipelineStage = stage, Title = "Existing", PriorityCode = "normal" };
        db.PipelineStages.AddRange(stage, archivedStage);
        db.Opportunities.Add(opportunity);
        await db.SaveChangesAsync();
        var before = await ChildrenSnapshotAsync(db);
        Assert.Single((await GetAsync(pipeline.Id)).Stages);
        var allStages = (await GetAsync(pipeline.Id, true)).Stages;
        Assert.Equal(new[] { archivedStage.Id, stage.Id }, allStages.Select(x => x.Id));
        var list = (await client.GetFromJsonAsync<List<PipelineDto>>("/api/pipelines?includeArchived=true"))!;
        Assert.Equal(allStages.Select(x => x.Id), Assert.Single(list).Stages.Select(x => x.Id));
        await ActionAsync(pipeline.Id, "set-default");
        await ActionAsync(pipeline.Id, "archive");
        var archivedDetail = await GetAsync(pipeline.Id);
        Assert.NotNull(archivedDetail.ArchivedAt);
        Assert.Equal(stage.Id, Assert.Single(archivedDetail.Stages).Id);
        var archivedDetailWithStages = await GetAsync(pipeline.Id, includeArchivedStages: true);
        Assert.NotNull(archivedDetailWithStages.ArchivedAt);
        Assert.Equal(new[] { archivedStage.Id, stage.Id }, archivedDetailWithStages.Stages.Select(x => x.Id));
        Assert.Equal(before, await ChildrenSnapshotAsync(db));
        var opportunities = new OpportunityService(db, new CurrentWorkspaceProvider(db));
        var rejected = await opportunities.CreateAsync(new CreateOpportunityRequest
        {
            Title = "New", PipelineStageId = stage.Id, PriorityCode = "normal"
        }, default);
        Assert.NotNull(rejected.Error);
        var existing = await opportunities.UpdateAsync(opportunity.Id, new UpdateOpportunityRequest
        {
            Title = "Still editable", PipelineStageId = stage.Id, PriorityCode = "normal"
        }, default);
        Assert.True(existing.Found);
        Assert.Null(existing.Error);
        var reassignment = await opportunities.UpdateAsync(opportunity.Id, new UpdateOpportunityRequest
        {
            Title = "Rejected", PipelineStageId = archivedStage.Id, PriorityCode = "normal"
        }, default);
        Assert.NotNull(reassignment.Error);
        await ActionAsync(pipeline.Id, "restore");
        Assert.NotNull((await db.PipelineStages.AsNoTracking().SingleAsync(x => x.Id == archivedStage.Id)).ArchivedAt);
        var archivedRejected = await opportunities.CreateAsync(new CreateOpportunityRequest
        {
            Title = "New", PipelineStageId = archivedStage.Id, PriorityCode = "normal"
        }, default);
        Assert.NotNull(archivedRejected.Error);
    }

    [Fact]
    public async Task CompositeForeignKeyRejectsCrossWorkspaceDefaultAndHardDeletion()
    {
        var local = await CreateAsync();
        await using var db = CreateContext();
        var other = new Workspace { OwnerUserId = (await db.UserAccounts.SingleAsync()).Id, Name = "Other", TimeZoneId = "UTC", ArchivedAt = DateTimeOffset.UtcNow };
        var foreign = new Pipeline { Workspace = other, Name = "Foreign", TypeCode = "custom" };
        db.Pipelines.Add(foreign);
        await db.SaveChangesAsync();
        var error = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE \"Workspaces\" SET \"DefaultPipelineId\" = {foreign.Id} WHERE \"Id\" = {workspaceId}"));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, error.SqlState);
        await ActionAsync(local.Id, "set-default");
        var deletion = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM \"Pipelines\" WHERE \"Id\" = {local.Id}"));
        Assert.Equal(PostgresErrorCodes.RestrictViolation, deletion.SqlState);
    }

    [Fact]
    public async Task FailedArchiveRollsBackClearingTheDefault()
    {
        var pipeline = await CreateAsync();
        await ActionAsync(pipeline.Id, "set-default");
        await using var db = CreateContext();
        // Force an archive failure only in the disposable test database.
        await db.Database.ExecuteSqlRawAsync(
            "ALTER TABLE \"Pipelines\" ADD CONSTRAINT reject_archive CHECK (\"ArchivedAt\" IS NULL)");
        var service = new PipelineService(db, new CurrentWorkspaceProvider(db));
        await Assert.ThrowsAsync<DbUpdateException>(() => service.ArchiveAsync(pipeline.Id, default));
        await using var check = CreateContext();
        Assert.Null((await check.Pipelines.SingleAsync()).ArchivedAt);
        Assert.Equal(pipeline.Id, (await check.Workspaces.SingleAsync()).DefaultPipelineId);
    }

    [Fact]
    public async Task MigrationPreservesExistingPipelineAndDefaultsVisibilityToTrue()
    {
        await using var db = CreateContext();
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync("20260925101154_InitialCrmSchema");
        var id = Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO \"Pipelines\" (\"Id\", \"WorkspaceId\", \"Name\", \"TypeCode\", \"CreatedAt\") VALUES ({id}, {workspaceId}, 'Existing', 'custom', {DateTimeOffset.UtcNow})");
        await migrator.MigrateAsync();
        Assert.True((await db.Pipelines.AsNoTracking().SingleAsync()).IsVisible);
        Assert.Null((await db.Workspaces.AsNoTracking().SingleAsync()).DefaultPipelineId);
        Assert.Equal(id, (await GetAsync(id)).Id);
    }

    [Fact]
    public async Task ConcurrentArchiveAndSetDefaultCannotLeaveAnArchivedDefault()
    {
        var pipeline = await CreateAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var token = timeout.Token;
        await using var blocker = CreateContext();
        await using var transaction = await blocker.Database.BeginTransactionAsync(token);
        await blocker.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"Workspaces\" WHERE \"Id\" = {workspaceId} FOR UPDATE", token);
        await using var firstDb = CreateContext();
        await using var secondDb = CreateContext();
        var archive = new PipelineService(firstDb, new CurrentWorkspaceProvider(firstDb)).ArchiveAsync(pipeline.Id, token);
        var setDefault = new PipelineService(secondDb, new CurrentWorkspaceProvider(secondDb)).SetDefaultAsync(pipeline.Id, token);
        await using var observer = new NpgsqlConnection(postgres.GetConnectionString());
        await observer.OpenAsync(token);
        await using var waiting = new NpgsqlCommand(
            "SELECT count(*) FROM pg_stat_activity WHERE datname = current_database() AND wait_event_type = 'Lock' AND query LIKE '%Workspaces%'", observer);
        while (Convert.ToInt64(await waiting.ExecuteScalarAsync(token)) < 2)
            await Task.Delay(25, token);
        Assert.False(archive.IsCompleted);
        Assert.False(setDefault.IsCompleted);
        await transaction.CommitAsync(token);
        Assert.True(await archive);
        Assert.True((await setDefault).Found);
        await using var check = CreateContext();
        Assert.NotNull((await check.Pipelines.SingleAsync(token)).ArchivedAt);
        Assert.Null((await check.Workspaces.SingleAsync(token)).DefaultPipelineId);
    }

    private static async Task<string> ChildrenSnapshotAsync(ProspectionCrmDbContext db)
        => System.Text.Json.JsonSerializer.Serialize(new
        {
            Stages = await db.PipelineStages.AsNoTracking().OrderBy(x => x.Id).ToListAsync(),
            Opportunities = await db.Opportunities.AsNoTracking().OrderBy(x => x.Id).ToListAsync()
        });
}
