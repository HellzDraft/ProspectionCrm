using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using ProspectionCrm.Api.Data;
using ProspectionCrm.Api.Dtos.Pipelines;
using ProspectionCrm.Api.Entities;
using ProspectionCrm.Api.Services;
using Testcontainers.PostgreSql;
using Xunit;

namespace ProspectionCrm.Api.Tests;

public sealed class InitialPipelineTests : IAsyncLifetime
{
    private const string Route = "/api/setup/initial-pipelines";
    private readonly PostgreSqlContainer postgres = new PostgreSqlBuilder("postgres:18").Build();
    private WebApplicationFactory<Program> factory = null!;
    private HttpClient client = null!;

    public async Task InitializeAsync()
    {
        await postgres.StartAsync();
        await using var db = CreateContext();
        await db.Database.MigrateAsync();
        factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder
            .UseEnvironment("Testing").UseSetting("ConnectionStrings:DefaultConnection", postgres.GetConnectionString()));
        client = factory.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
    }

    public async Task DisposeAsync()
    {
        client?.Dispose();
        if (factory is not null) await factory.DisposeAsync();
        await postgres.DisposeAsync();
    }

    private ProspectionCrmDbContext CreateContext() => new(
        new DbContextOptionsBuilder<ProspectionCrmDbContext>().UseNpgsql(postgres.GetConnectionString()).Options);

    private static InitialPipelineService Service(ProspectionCrmDbContext db)
    {
        var provider = new CurrentWorkspaceProvider(db);
        return new(db, provider, new PipelineService(db, provider));
    }

    private async Task<Guid> BootstrapAsync()
    {
        using var response = await client.PostAsync("/api/setup/bootstrap", null);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        await using var db = CreateContext();
        return (await db.Workspaces.SingleAsync()).Id;
    }

    private async Task<PipelineDto> InitializePipelineAsync(HttpStatusCode status = HttpStatusCode.Created)
    {
        using var response = await client.PostAsync(Route, null);
        Assert.Equal(status, response.StatusCode);
        var result = (await response.Content.ReadFromJsonAsync<PipelineDto>())!;
        if (status == HttpStatusCode.Created)
        {
            Assert.EndsWith($"/api/pipelines/{result.Id}", response.Headers.Location?.ToString());
            Assert.Equal(result.Id, (await client.GetFromJsonAsync<PipelineDto>(response.Headers.Location))!.Id);
        }
        return result;
    }

    private async Task<string> SnapshotAsync()
    {
        await using var db = CreateContext();
        await using var connection = new NpgsqlConnection(postgres.GetConnectionString());
        await connection.OpenAsync();
        var snapshot = new SortedDictionary<string, string>();
        foreach (var table in db.Model.GetEntityTypes().Select(x => x.GetTableName()!).Distinct().Order())
        {
            var quoted = table.Replace("\"", "\"\"");
            await using var command = new NpgsqlCommand(
                $"SELECT COALESCE(jsonb_agg(to_jsonb(t) ORDER BY to_jsonb(t)::text), '[]'::jsonb)::text FROM \"{quoted}\" t", connection);
            snapshot[table] = (string)(await command.ExecuteScalarAsync())!;
        }
        return JsonSerializer.Serialize(snapshot);
    }

    [Fact]
    public async Task ExplicitInitializationCreatesOnlyEmploymentConfigurationAfterBootstrap()
    {
        await using var db = CreateContext();
        Assert.False(db.Database.HasPendingModelChanges());
        Assert.Empty(await db.Workspaces.ToArrayAsync());
        Assert.Empty(await db.Pipelines.ToArrayAsync());
        Assert.Empty(await db.PipelineStages.ToArrayAsync());
        var workspaceId = await BootstrapAsync();
        Assert.Empty(await db.Pipelines.ToArrayAsync());
        Assert.Empty(await db.PipelineStages.ToArrayAsync());
        Assert.Empty(await db.Opportunities.ToArrayAsync());
        var result = await InitializePipelineAsync();
        Assert.Equal("Emploi .NET", result.Name);
        Assert.Equal("employment", result.TypeCode);
        Assert.True(result.IsVisible);
        Assert.True(result.IsDefault);
        Assert.Null(result.ArchivedAt);
        Assert.Null(result.PreferredCandidateProfileId);
        Assert.Equal("Pipeline de prospection pour les offres d'emploi .NET / C#, principalement autour de Bordeaux et en remote France/Europe.", result.Description);
        Assert.Equal(new[] { "À analyser", "À candidater", "Candidature envoyée", "Entretien", "Offre", "Refusé", "Abandonné" }, result.Stages.Select(x => x.Name));
        Assert.Equal(new[] { "active", "active", "active", "active", "success", "failure", "failure" }, result.Stages.Select(x => x.CategoryCode));
        Assert.Equal(Enumerable.Range(0, 7), result.Stages.Select(x => x.SortOrder));
        Assert.All(result.Stages, stage => { Assert.Null(stage.Description); Assert.Null(stage.ArchivedAt); Assert.Equal(result.Id, stage.PipelineId); });
        Assert.Equal(workspaceId, (await db.Pipelines.SingleAsync()).WorkspaceId);
        Assert.Equal(result.Id, (await db.Workspaces.AsNoTracking().SingleAsync()).DefaultPipelineId);
        // Every other modeled table remains empty, except the owner/workspace from bootstrap.
        await using var connection = new NpgsqlConnection(postgres.GetConnectionString());
        await connection.OpenAsync();
        foreach (var table in db.Model.GetEntityTypes().Select(x => x.GetTableName()!).Distinct()
            .Except(new[] { "UserAccounts", "Workspaces", "Pipelines", "PipelineStages" }))
        {
            await using var command = new NpgsqlCommand($"SELECT count(*) FROM \"{table.Replace("\"", "\"\"")}\"", connection);
            Assert.Equal(0L, await command.ExecuteScalarAsync());
        }
        var before = await SnapshotAsync();
        Assert.Equal(result.Id, (await InitializePipelineAsync(HttpStatusCode.OK)).Id);
        Assert.Equal(before, await SnapshotAsync());
        using var bootstrapAgain = await client.PostAsync("/api/setup/bootstrap", null);
        Assert.Equal(HttpStatusCode.OK, bootstrapAgain.StatusCode);
        Assert.Equal(before, await SnapshotAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RepeatedInitializationPreservesUserChangesIncludingRenamingAndArchives(bool archived)
    {
        await BootstrapAsync();
        var initial = await InitializePipelineAsync();
        using (var update = await client.PutAsJsonAsync($"/api/pipelines/{initial.Id}", new
        { name = "My renamed pipeline", typeCode = "custom", description = "User text", isVisible = false }))
            Assert.Equal(HttpStatusCode.NoContent, update.StatusCode);
        using (var update = await client.PutAsJsonAsync($"/api/pipelines/{initial.Id}/stages/{initial.Stages[0].Id}", new
        { name = "My stage", categoryCode = "success", description = "Keep" }))
            Assert.Equal(HttpStatusCode.NoContent, update.StatusCode);
        using (var archiveStage = await client.PostAsync($"/api/pipelines/{initial.Id}/stages/{initial.Stages[1].Id}/archive", null))
            Assert.Equal(HttpStatusCode.NoContent, archiveStage.StatusCode);
        if (archived)
        {
            using var archive = await client.PostAsync($"/api/pipelines/{initial.Id}/archive", null);
            Assert.Equal(HttpStatusCode.NoContent, archive.StatusCode);
        }
        else
        {
            await using var db = CreateContext();
            await db.Workspaces.ExecuteUpdateAsync(x => x.SetProperty(w => w.DefaultPipelineId, (Guid?)null));
        }
        var before = await SnapshotAsync();
        var repeated = await InitializePipelineAsync(HttpStatusCode.OK);
        Assert.Equal(initial.Id, repeated.Id);
        Assert.Equal("My renamed pipeline", repeated.Name);
        Assert.False(repeated.IsDefault);
        Assert.Equal(archived, repeated.ArchivedAt.HasValue);
        Assert.Equal(7, repeated.Stages.Count);
        Assert.Equal(before, await SnapshotAsync());
    }

    [Fact]
    public async Task InitialIdentityConventionRemainsStableAcrossReleases()
    {
        await using var db = CreateContext();
        db.Workspaces.Add(new Workspace
        {
            Id = Guid.Parse("11111111-2222-3333-4444-555555555555"), Name = "Fixed identity", TimeZoneId = "UTC",
            OwnerUser = new UserAccount { Email = "identity@example.invalid" }
        });
        await db.SaveChangesAsync();
        var initial = await InitializePipelineAsync();
        // Persisted identity compatibility vector: changing this would duplicate already initialized pipelines.
        Assert.Equal(Guid.Parse("a30b7775-d6de-8404-adb7-d7f12a07d164"), initial.Id);
    }

    [Fact]
    public async Task ExistingDifferentDefaultIsNeverReplaced()
    {
        var workspaceId = await BootstrapAsync();
        await using var db = CreateContext();
        var other = new Pipeline { WorkspaceId = workspaceId, Name = "Chosen", TypeCode = "custom" };
        db.Pipelines.Add(other);
        await db.SaveChangesAsync();
        (await db.Workspaces.SingleAsync()).DefaultPipelineId = other.Id;
        await db.SaveChangesAsync();
        Assert.False((await InitializePipelineAsync()).IsDefault);
        var before = await SnapshotAsync();
        await InitializePipelineAsync(HttpStatusCode.OK);
        Assert.Equal(before, await SnapshotAsync());
        Assert.Equal(other.Id, (await db.Workspaces.AsNoTracking().SingleAsync()).DefaultPipelineId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnrecognizedHomonymsAreNotAdoptedOrDuplicated(bool archived)
    {
        var workspaceId = await BootstrapAsync();
        await using var db = CreateContext();
        db.Pipelines.Add(new Pipeline
        {
            WorkspaceId = workspaceId, Name = "Emploi .NET", TypeCode = "employment", ArchivedAt = archived ? DateTimeOffset.UtcNow : null
        });
        await db.SaveChangesAsync();
        var before = await SnapshotAsync();
        using var response = await client.PostAsync(Route, null);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(before, await SnapshotAsync());
    }

    [Fact]
    public async Task IdentityAndDefaultAreScopedToWorkspace()
    {
        var firstWorkspace = await BootstrapAsync();
        var first = await InitializePipelineAsync();
        await using var db = CreateContext();
        await db.Workspaces.ExecuteUpdateAsync(x => x.SetProperty(w => w.ArchivedAt, DateTimeOffset.UtcNow));
        var secondWorkspace = new Workspace { OwnerUserId = (await db.UserAccounts.SingleAsync()).Id, Name = "Other", TimeZoneId = "UTC" };
        db.Workspaces.Add(secondWorkspace);
        await db.SaveChangesAsync();
        var second = await InitializePipelineAsync();
        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(secondWorkspace.Id, (await db.Pipelines.SingleAsync(x => x.Id == second.Id)).WorkspaceId);
        Assert.Equal(firstWorkspace, (await db.Pipelines.SingleAsync(x => x.Id == first.Id)).WorkspaceId);
        Assert.Equal(first.Id, (await db.Workspaces.AsNoTracking().SingleAsync(x => x.Id == firstWorkspace)).DefaultPipelineId);
        Assert.Equal(second.Id, (await db.Workspaces.AsNoTracking().SingleAsync(x => x.Id == secondWorkspace.Id)).DefaultPipelineId);
        Assert.Equal(14, await db.PipelineStages.CountAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IncompatibleWorkspaceStateReturnsConflictWithoutWrites(bool multiple)
    {
        if (multiple)
        {
            await BootstrapAsync();
            await using var db = CreateContext();
            db.Workspaces.Add(new Workspace { OwnerUserId = (await db.UserAccounts.SingleAsync()).Id, Name = "Other", TimeZoneId = "UTC" });
            await db.SaveChangesAsync();
        }
        var before = await SnapshotAsync();
        using var response = await client.PostAsync(Route, null);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(before, await SnapshotAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedStageOrDefaultWriteRollsBackAllInitialization(bool failDefault)
    {
        await BootstrapAsync();
        var before = await SnapshotAsync();
        await using var db = CreateContext();
        await db.Database.ExecuteSqlRawAsync(failDefault
            ? "ALTER TABLE \"Workspaces\" ADD CONSTRAINT reject_initial_default CHECK (\"DefaultPipelineId\" IS NULL)"
            : "ALTER TABLE \"PipelineStages\" ADD CONSTRAINT reject_initial_stage CHECK (\"SortOrder\" <> 6)");
        await Assert.ThrowsAsync<DbUpdateException>(() => Service(db).InitializeAsync(default));
        Assert.Equal(before, await SnapshotAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentInitializersWaitForWorkspaceAndCreateExactlyOnePipeline(bool defaultChosenWhileWaiting)
    {
        var workspaceId = await BootstrapAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        await using var blocker = CreateContext();
        var chosen = new Pipeline { WorkspaceId = workspaceId, Name = "Chosen concurrently", TypeCode = "custom" };
        if (defaultChosenWhileWaiting)
        {
            blocker.Pipelines.Add(chosen);
            await blocker.SaveChangesAsync(token);
        }
        await using var transaction = await blocker.Database.BeginTransactionAsync(token);
        await blocker.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"Workspaces\" WHERE \"Id\" = {workspaceId} FOR NO KEY UPDATE", token);
        await using var firstDb = CreateContext();
        await using var secondDb = CreateContext();
        var first = Service(firstDb).InitializeAsync(token);
        var second = Service(secondDb).InitializeAsync(token);
        await using var observer = new NpgsqlConnection(postgres.GetConnectionString());
        await observer.OpenAsync(token);
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM pg_stat_activity WHERE datname = current_database() AND wait_event_type = 'Lock' AND query LIKE '%Workspaces%'", observer);
        while (Convert.ToInt64(await command.ExecuteScalarAsync(token)) < 2) await Task.Delay(25, token);
        Assert.False(first.IsCompleted);
        Assert.False(second.IsCompleted);
        if (defaultChosenWhileWaiting)
        {
            (await blocker.Workspaces.SingleAsync(token)).DefaultPipelineId = chosen.Id;
            await blocker.SaveChangesAsync(token);
        }
        await transaction.CommitAsync(token);
        var results = await Task.WhenAll(first, second);
        Assert.All(results, x => Assert.Null(x.Error));
        Assert.Single(results, x => x.Created);
        Assert.Equal(results[0].Pipeline!.Id, results[1].Pipeline!.Id);
        await using var check = CreateContext();
        Assert.Equal(defaultChosenWhileWaiting ? 2 : 1, await check.Pipelines.CountAsync(token));
        Assert.Equal(7, await check.PipelineStages.CountAsync(token));
        Assert.Equal(defaultChosenWhileWaiting ? chosen.Id : results[0].Pipeline!.Id,
            (await check.Workspaces.SingleAsync(token)).DefaultPipelineId);
    }
}
