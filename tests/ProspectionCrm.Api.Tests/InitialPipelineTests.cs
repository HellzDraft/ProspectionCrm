using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using ProspectionCrm.Api.Data;
using ProspectionCrm.Api.Dtos.Pipelines;
using ProspectionCrm.Api.Dtos.Setup;
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

    private async Task<InitialPipelinesDto> InitializePipelinesAsync(HttpStatusCode status = HttpStatusCode.Created)
    {
        using var response = await client.PostAsync(Route, null);
        Assert.Equal(status, response.StatusCode);
        Assert.Null(response.Headers.Location);
        var result = (await response.Content.ReadFromJsonAsync<InitialPipelinesDto>())!;
        Assert.Equal(3, result.Pipelines.Count);
        Assert.Equal(3, result.Pipelines.Select(x => x.Id).Distinct().Count());
        Assert.All(result.CreatedPipelineIds, id => Assert.Contains(result.Pipelines, p => p.Id == id));
        if (status == HttpStatusCode.Created) Assert.NotEmpty(result.CreatedPipelineIds);
        else Assert.Empty(result.CreatedPipelineIds);
        foreach (var pipeline in result.Pipelines)
            Assert.Equal(pipeline.Id, (await client.GetFromJsonAsync<PipelineDto>($"/api/pipelines/{pipeline.Id}"))!.Id);
        return result;
    }

    private async Task<string> SnapshotAsync(params Guid[] excludePipelineIds)
    {
        await using var db = CreateContext();
        await using var connection = new NpgsqlConnection(postgres.GetConnectionString());
        await connection.OpenAsync();
        var snapshot = new SortedDictionary<string, string>();
        foreach (var table in db.Model.GetEntityTypes().Select(x => x.GetTableName()!).Distinct().Order())
        {
            var quoted = table.Replace("\"", "\"\"");
            var filter = excludePipelineIds.Length == 0 ? "" : table switch
            {
                "Pipelines" => " WHERE \"Id\" <> ALL(@excluded)",
                "PipelineStages" => " WHERE \"PipelineId\" <> ALL(@excluded)",
                _ => ""
            };
            await using var command = new NpgsqlCommand(
                $"SELECT COALESCE(jsonb_agg(to_jsonb(t) ORDER BY to_jsonb(t)::text), '[]'::jsonb)::text FROM \"{quoted}\" t{filter}", connection);
            if (excludePipelineIds.Length > 0) command.Parameters.AddWithValue("excluded", excludePipelineIds);
            snapshot[table] = (string)(await command.ExecuteScalarAsync())!;
        }
        return JsonSerializer.Serialize(snapshot);
    }

    // Persist a Phase 5.2 state directly, without calling the new initializer or its identity helper.
    private async Task<Guid> SeedPhase52Async(string defaultState)
    {
        await using var db = CreateContext();
        var workspace = new Workspace
        {
            Id = Guid.Parse("11111111-2222-3333-4444-555555555555"), Name = "Phase 5.2", TimeZoneId = "UTC",
            OwnerUser = new UserAccount { Email = "upgrade@example.invalid" }
        };
        var employment = new Pipeline
        {
            Id = Guid.Parse("a30b7775-d6de-8404-adb7-d7f12a07d164"), Workspace = workspace,
            Name = "Renamed before upgrade", TypeCode = "custom", IsVisible = false, Description = "User description",
            CreatedAt = DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
            UpdatedAt = DateTimeOffset.Parse("2026-01-02T00:00:00Z"),
            ArchivedAt = defaultState == "archived" ? DateTimeOffset.Parse("2026-01-03T00:00:00Z") : null,
            Stages = Enumerable.Range(0, 7).Select(i => new PipelineStage
            {
                Name = $"User stage {i}", CategoryCode = i == 0 ? "success" : "active", Description = "Keep",
                SortOrder = 6 - i, ArchivedAt = i == 1 ? DateTimeOffset.Parse("2026-01-02T00:00:00Z") : null
            }).ToList()
        };
        db.Pipelines.Add(employment);
        await db.SaveChangesAsync();
        if (defaultState == "employment") workspace.DefaultPipelineId = employment.Id;
        if (defaultState == "other")
        {
            var other = new Pipeline { WorkspaceId = workspace.Id, Name = "User default", TypeCode = "custom" };
            db.Pipelines.Add(other);
            await db.SaveChangesAsync();
            workspace.DefaultPipelineId = other.Id;
        }
        await db.SaveChangesAsync();
        return employment.Id;
    }

    // Frozen Phase 5.3 data: no use of the current initializer or identity generator.
    private async Task<Guid> SeedPhase53Async(string defaultState)
    {
        await using var db = CreateContext();
        var workspace = new Workspace
        {
            Id = Guid.Parse("11111111-2222-3333-4444-555555555555"), Name = "Phase 5.3", TimeZoneId = "UTC",
            OwnerUser = new UserAccount { Email = "upgrade53@example.invalid" }
        };
        var employment = new Pipeline
        {
            Id = Guid.Parse("a30b7775-d6de-8404-adb7-d7f12a07d164"), Workspace = workspace,
            Name = "Emploi .NET", TypeCode = "employment", IsVisible = true,
            Description = "Pipeline de prospection pour les offres d'emploi .NET / C#, principalement autour de Bordeaux et en remote France/Europe."
        };
        var freelance = new Pipeline
        {
            Id = Guid.Parse("facae35b-9c73-8a26-b928-aa1767b5d199"), Workspace = workspace,
            Name = "Freelance / Malt", TypeCode = "freelance", IsVisible = true,
            Description = "Pipeline de prospection pour les missions freelance C# / .NET / ASP.NET Core et Unity, principalement en remote ou autour de Bordeaux."
        };
        var employmentStages = new[] { "À analyser", "À candidater", "Candidature envoyée", "Entretien", "Offre", "Refusé", "Abandonné" };
        var freelanceStages = new[] { "À analyser", "À contacter", "Proposition envoyée", "Échange client", "Mission gagnée", "Refusée / perdue", "Abandonnée" };
        var categories = new[] { "active", "active", "active", "active", "success", "failure", "failure" };
        employment.Stages = employmentStages.Select((name, i) => new PipelineStage { Name = name, CategoryCode = categories[i], SortOrder = i }).ToList();
        freelance.Stages = freelanceStages.Select((name, i) => new PipelineStage { Name = name, CategoryCode = categories[i], SortOrder = i }).ToList();
        db.Pipelines.AddRange(employment, freelance);
        await db.SaveChangesAsync();
        if (defaultState is "employment" or "freelance-archived") workspace.DefaultPipelineId = employment.Id;
        if (defaultState == "other")
        {
            var other = new Pipeline { WorkspaceId = workspace.Id, Name = "Chosen by user", TypeCode = "custom" };
            db.Pipelines.Add(other);
            await db.SaveChangesAsync();
            workspace.DefaultPipelineId = other.Id;
        }
        if (defaultState == "employment-archived") employment.ArchivedAt = DateTimeOffset.UtcNow;
        if (defaultState == "freelance-archived") freelance.ArchivedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        return workspace.Id;
    }

    [Theory]
    [InlineData("employment", false)]
    [InlineData("employment", true)]
    [InlineData("removed", true)]
    [InlineData("other", true)]
    [InlineData("employment-archived", true)]
    [InlineData("freelance-archived", true)]
    public async Task Phase53UpgradeCreatesOnlyGameEmploymentAndPreservesAllExistingData(string defaultState, bool modified)
    {
        await SeedPhase53Async(defaultState);
        if (modified)
        {
            await using var db = CreateContext();
            var ids = new[] { Guid.Parse("a30b7775-d6de-8404-adb7-d7f12a07d164"), Guid.Parse("facae35b-9c73-8a26-b928-aa1767b5d199") };
            await db.Pipelines.Where(p => ids.Contains(p.Id)).ExecuteUpdateAsync(p => p
                .SetProperty(x => x.Name, x => "Renamed " + x.Name).SetProperty(x => x.TypeCode, "custom")
                .SetProperty(x => x.Description, "User text").SetProperty(x => x.IsVisible, false)
                .SetProperty(x => x.UpdatedAt, DateTimeOffset.Parse("2026-01-02T00:00:00Z")));
            // Use disjoint positions so immediate unique-order constraints stay valid.
            await db.PipelineStages.Where(s => ids.Contains(s.PipelineId)).ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Name, x => "Changed " + x.Name).SetProperty(x => x.Description, "Keep")
                .SetProperty(x => x.CategoryCode, "success").SetProperty(x => x.SortOrder, x => 20 - x.SortOrder)
                .SetProperty(x => x.ArchivedAt, DateTimeOffset.Parse("2026-01-03T00:00:00Z")));
        }
        var before = await SnapshotAsync();
        var result = await InitializePipelinesAsync();
        Assert.Equal(Guid.Parse("a30b7775-d6de-8404-adb7-d7f12a07d164"), result.Pipelines[0].Id);
        Assert.Equal(Guid.Parse("facae35b-9c73-8a26-b928-aa1767b5d199"), result.Pipelines[1].Id);
        var game = result.Pipelines[2];
        Assert.Equal("Emploi Jeu Vidéo", game.Name);
        Assert.Equal(7, game.Stages.Count);
        Assert.False(game.IsDefault);
        Assert.Equal(new[] { game.Id }, result.CreatedPipelineIds);
        Assert.Equal(before, await SnapshotAsync(game.Id));
        var upgraded = await SnapshotAsync();
        await InitializePipelinesAsync(HttpStatusCode.OK);
        Assert.Equal(upgraded, await SnapshotAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Phase53UpgradeWithGameEmploymentHomonymLeavesExistingDataUnchanged(bool archived)
    {
        var workspaceId = await SeedPhase53Async("removed");
        await using var db = CreateContext();
        db.Pipelines.Add(new Pipeline
        {
            WorkspaceId = workspaceId, Name = "Emploi Jeu Vidéo", TypeCode = "employment",
            ArchivedAt = archived ? DateTimeOffset.UtcNow : null
        });
        await db.SaveChangesAsync();
        var before = await SnapshotAsync();
        using var response = await client.PostAsync(Route, null);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(before, await SnapshotAsync());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task FailedGameEmploymentPipelineOrStageRollsBackEntireCall(bool upgrade, bool failStage)
    {
        if (upgrade) await SeedPhase53Async("employment");
        else
        {
            // Fixed workspace keeps the failure specific to the third template's reserved ID.
            await using var seed = CreateContext();
            seed.Workspaces.Add(new Workspace
            {
                Id = Guid.Parse("11111111-2222-3333-4444-555555555555"), Name = "Fresh", TimeZoneId = "UTC",
                OwnerUser = new UserAccount { Email = "rollback54@example.invalid" }
            });
            await seed.SaveChangesAsync();
        }
        var before = await SnapshotAsync();
        await using var db = CreateContext();
        await db.Database.ExecuteSqlRawAsync(failStage
            ? "ALTER TABLE \"PipelineStages\" ADD CONSTRAINT reject_game_stage CHECK (\"PipelineId\" <> '66d1f9d1-d3fc-816b-b2b7-c32a9c45604e'::uuid OR \"SortOrder\" <> 6)"
            : "ALTER TABLE \"Pipelines\" ADD CONSTRAINT reject_game_pipeline CHECK (\"Id\" <> '66d1f9d1-d3fc-816b-b2b7-c32a9c45604e'::uuid)");
        await Assert.ThrowsAsync<DbUpdateException>(() => Service(db).InitializeAsync(default));
        Assert.Equal(before, await SnapshotAsync());
    }

    [Theory]
    [InlineData("employment")]
    [InlineData("removed")]
    [InlineData("other")]
    [InlineData("archived")]
    public async Task Phase52UpgradeCreatesMissingTemplatesAndPreservesAllExistingData(string defaultState)
    {
        var employmentId = await SeedPhase52Async(defaultState);
        var before = await SnapshotAsync();
        var result = await InitializePipelinesAsync();
        Assert.Equal(employmentId, result.Pipelines[0].Id);
        Assert.Equal(new[] { result.Pipelines[1].Id, result.Pipelines[2].Id }, result.CreatedPipelineIds);
        Assert.Equal("Freelance / Malt", result.Pipelines[1].Name);
        Assert.Equal(7, result.Pipelines[1].Stages.Count);
        Assert.False(result.Pipelines[1].IsDefault);
        Assert.False(result.Pipelines[2].IsDefault);
        Assert.Equal(before, await SnapshotAsync(result.Pipelines[1].Id, result.Pipelines[2].Id));
        var upgraded = await SnapshotAsync();
        await InitializePipelinesAsync(HttpStatusCode.OK);
        Assert.Equal(upgraded, await SnapshotAsync());
    }

    [Fact]
    public async Task Phase52UpgradeWithFreelanceHomonymLeavesExistingDataUnchanged()
    {
        await SeedPhase52Async("removed");
        await using var db = CreateContext();
        db.Pipelines.Add(new Pipeline
        {
            WorkspaceId = (await db.Workspaces.SingleAsync()).Id, Name = "Freelance / Malt", TypeCode = "freelance"
        });
        await db.SaveChangesAsync();
        var before = await SnapshotAsync();
        using var response = await client.PostAsync(Route, null);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(before, await SnapshotAsync());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task FailedFreelancePipelineOrStageRollsBackEntireCall(bool upgrade, bool failStage)
    {
        if (upgrade) await SeedPhase52Async("employment");
        else await BootstrapAsync();
        var before = await SnapshotAsync();
        await using var db = CreateContext();
        await db.Database.ExecuteSqlRawAsync(failStage
            ? "ALTER TABLE \"PipelineStages\" ADD CONSTRAINT reject_freelance_stage CHECK (\"Name\" <> 'Mission gagnée')"
            : "ALTER TABLE \"Pipelines\" ADD CONSTRAINT reject_freelance_pipeline CHECK (\"TypeCode\" <> 'freelance')");
        await Assert.ThrowsAsync<DbUpdateException>(() => Service(db).InitializeAsync(default));
        Assert.Equal(before, await SnapshotAsync());
    }

    [Theory]
    [InlineData("a30b7775-d6de-8404-adb7-d7f12a07d164")]
    [InlineData("facae35b-9c73-8a26-b928-aa1767b5d199")]
    [InlineData("66d1f9d1-d3fc-816b-b2b7-c32a9c45604e")]
    public async Task ReservedIdentityInAnotherWorkspaceReturnsConflictWithoutWrites(string reservedId)
    {
        await using var db = CreateContext();
        var current = new Workspace
        {
            Id = Guid.Parse("11111111-2222-3333-4444-555555555555"), Name = "Current", TimeZoneId = "UTC",
            OwnerUser = new UserAccount { Email = "current@example.invalid" }
        };
        var other = new Workspace
        {
            Name = "Other", TimeZoneId = "UTC", ArchivedAt = DateTimeOffset.UtcNow, OwnerUser = current.OwnerUser
        };
        db.Workspaces.Add(current);
        db.Pipelines.Add(new Pipeline { Id = Guid.Parse(reservedId), Workspace = other, Name = "Foreign", TypeCode = "custom" });
        await db.SaveChangesAsync();
        var before = await SnapshotAsync();
        using var response = await client.PostAsync(Route, null);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(before, await SnapshotAsync());
    }

    [Fact]
    public async Task ExplicitInitializationCreatesThreeConfigurationsOnlyAfterBootstrap()
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
        var initialized = await InitializePipelinesAsync();
        var result = initialized.Pipelines[0];
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
        Assert.Equal(initialized.Pipelines.Select(x => x.Id), initialized.CreatedPipelineIds);
        Assert.Equal(3, await db.Pipelines.CountAsync());
        Assert.Equal(21, await db.PipelineStages.CountAsync());
        Assert.All(await db.Pipelines.ToArrayAsync(), p => Assert.Equal(workspaceId, p.WorkspaceId));
        var freelance = initialized.Pipelines[1];
        Assert.Equal("Freelance / Malt", freelance.Name);
        Assert.Equal("freelance", freelance.TypeCode);
        Assert.True(freelance.IsVisible);
        Assert.False(freelance.IsDefault);
        Assert.Null(freelance.ArchivedAt);
        Assert.Null(freelance.PreferredCandidateProfileId);
        Assert.Equal("Pipeline de prospection pour les missions freelance C# / .NET / ASP.NET Core et Unity, principalement en remote ou autour de Bordeaux.", freelance.Description);
        Assert.Equal(new[] { "À analyser", "À contacter", "Proposition envoyée", "Échange client", "Mission gagnée", "Refusée / perdue", "Abandonnée" }, freelance.Stages.Select(x => x.Name));
        Assert.Equal(new[] { "active", "active", "active", "active", "success", "failure", "failure" }, freelance.Stages.Select(x => x.CategoryCode));
        Assert.Equal(Enumerable.Range(0, 7), freelance.Stages.Select(x => x.SortOrder));
        Assert.All(freelance.Stages, stage => { Assert.Null(stage.Description); Assert.Null(stage.ArchivedAt); Assert.Equal(freelance.Id, stage.PipelineId); });
        var game = initialized.Pipelines[2];
        Assert.Equal("Emploi Jeu Vidéo", game.Name);
        Assert.Equal("employment", game.TypeCode);
        Assert.True(game.IsVisible);
        Assert.False(game.IsDefault);
        Assert.Null(game.ArchivedAt);
        Assert.Null(game.PreferredCandidateProfileId);
        Assert.Equal("Pipeline de prospection pour les offres d'emploi jeu vidéo Unity / C#, principalement en France ou en remote Europe.", game.Description);
        Assert.Equal(new[] { "À analyser", "À candidater", "Candidature envoyée", "Entretien", "Offre", "Refusé", "Abandonné" }, game.Stages.Select(x => x.Name));
        Assert.Equal(new[] { "active", "active", "active", "active", "success", "failure", "failure" }, game.Stages.Select(x => x.CategoryCode));
        Assert.Equal(Enumerable.Range(0, 7), game.Stages.Select(x => x.SortOrder));
        Assert.All(game.Stages, stage => { Assert.Null(stage.Description); Assert.Null(stage.ArchivedAt); Assert.Equal(game.Id, stage.PipelineId); });
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
        Assert.Equal(result.Id, (await InitializePipelinesAsync(HttpStatusCode.OK)).Pipelines[0].Id);
        Assert.Equal(before, await SnapshotAsync());
        using var bootstrapAgain = await client.PostAsync("/api/setup/bootstrap", null);
        Assert.Equal(HttpStatusCode.OK, bootstrapAgain.StatusCode);
        Assert.Equal(before, await SnapshotAsync());
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(2, true)]
    public async Task RepeatedInitializationPreservesUserChangesIncludingRenamingAndArchives(int pipelineIndex, bool archived)
    {
        await BootstrapAsync();
        var initial = (await InitializePipelinesAsync()).Pipelines[pipelineIndex];
        using (var update = await client.PutAsJsonAsync($"/api/pipelines/{initial.Id}", new
        { name = "My renamed pipeline", typeCode = "custom", description = "User text", isVisible = false }))
            Assert.Equal(HttpStatusCode.NoContent, update.StatusCode);
        using (var update = await client.PutAsJsonAsync($"/api/pipelines/{initial.Id}/stages/{initial.Stages[0].Id}", new
        { name = "My stage", categoryCode = "success", description = "Keep" }))
            Assert.Equal(HttpStatusCode.NoContent, update.StatusCode);
        using (var reorder = await client.PutAsJsonAsync($"/api/pipelines/{initial.Id}/stages/order", new
        { stageIds = initial.Stages.Reverse().Select(x => x.Id).ToArray() }))
            Assert.Equal(HttpStatusCode.NoContent, reorder.StatusCode);
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
        var repeated = (await InitializePipelinesAsync(HttpStatusCode.OK)).Pipelines[pipelineIndex];
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
        var initial = await InitializePipelinesAsync();
        // Persisted identity compatibility vector: changing this would duplicate already initialized pipelines.
        Assert.Equal(Guid.Parse("a30b7775-d6de-8404-adb7-d7f12a07d164"), initial.Pipelines[0].Id);
        Assert.Equal(Guid.Parse("facae35b-9c73-8a26-b928-aa1767b5d199"), initial.Pipelines[1].Id);
        Assert.Equal(Guid.Parse("66d1f9d1-d3fc-816b-b2b7-c32a9c45604e"), initial.Pipelines[2].Id);
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
        Assert.All((await InitializePipelinesAsync()).Pipelines, p => Assert.False(p.IsDefault));
        var before = await SnapshotAsync();
        await InitializePipelinesAsync(HttpStatusCode.OK);
        Assert.Equal(before, await SnapshotAsync());
        Assert.Equal(other.Id, (await db.Workspaces.AsNoTracking().SingleAsync()).DefaultPipelineId);
    }

    [Theory]
    [InlineData("Emploi .NET", false)]
    [InlineData("Emploi .NET", true)]
    [InlineData("Freelance / Malt", false)]
    [InlineData("Freelance / Malt", true)]
    [InlineData("Emploi Jeu Vidéo", false)]
    [InlineData("Emploi Jeu Vidéo", true)]
    public async Task UnrecognizedHomonymsAreNotAdoptedOrDuplicated(string name, bool archived)
    {
        var workspaceId = await BootstrapAsync();
        await using var db = CreateContext();
        db.Pipelines.Add(new Pipeline
        {
            WorkspaceId = workspaceId, Name = name, TypeCode = "custom", ArchivedAt = archived ? DateTimeOffset.UtcNow : null
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
        var first = await InitializePipelinesAsync();
        await using var db = CreateContext();
        await db.Workspaces.ExecuteUpdateAsync(x => x.SetProperty(w => w.ArchivedAt, DateTimeOffset.UtcNow));
        var secondWorkspace = new Workspace { OwnerUserId = (await db.UserAccounts.SingleAsync()).Id, Name = "Other", TimeZoneId = "UTC" };
        db.Workspaces.Add(secondWorkspace);
        await db.SaveChangesAsync();
        var second = await InitializePipelinesAsync();
        Assert.Equal(6, first.Pipelines.Concat(second.Pipelines).Select(p => p.Id).Distinct().Count());
        foreach (var pipeline in first.Pipelines)
            Assert.Equal(firstWorkspace, (await db.Pipelines.SingleAsync(x => x.Id == pipeline.Id)).WorkspaceId);
        foreach (var pipeline in second.Pipelines)
            Assert.Equal(secondWorkspace.Id, (await db.Pipelines.SingleAsync(x => x.Id == pipeline.Id)).WorkspaceId);
        Assert.Equal(first.Pipelines[0].Id, (await db.Workspaces.AsNoTracking().SingleAsync(x => x.Id == firstWorkspace)).DefaultPipelineId);
        Assert.Equal(second.Pipelines[0].Id, (await db.Workspaces.AsNoTracking().SingleAsync(x => x.Id == secondWorkspace.Id)).DefaultPipelineId);
        Assert.Equal(42, await db.PipelineStages.CountAsync());
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
    [InlineData(false, "none")]
    [InlineData(false, "before")]
    [InlineData(false, "waiting")]
    [InlineData(true, "none")]
    [InlineData(true, "before")]
    [InlineData(true, "waiting")]
    public async Task ConcurrentInitializersWaitForWorkspaceAndCreateExactlyOneOfEachPipeline(bool upgrade, string defaultChoice)
    {
        var workspaceId = upgrade ? await SeedPhase53Async("removed") : await BootstrapAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        await using var blocker = CreateContext();
        var chosen = new Pipeline { WorkspaceId = workspaceId, Name = "Chosen concurrently", TypeCode = "custom" };
        if (defaultChoice != "none")
        {
            blocker.Pipelines.Add(chosen);
            await blocker.SaveChangesAsync(token);
        }
        if (defaultChoice == "before")
        {
            (await blocker.Workspaces.SingleAsync(token)).DefaultPipelineId = chosen.Id;
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
        if (defaultChoice == "waiting")
        {
            (await blocker.Workspaces.SingleAsync(token)).DefaultPipelineId = chosen.Id;
            await blocker.SaveChangesAsync(token);
        }
        await transaction.CommitAsync(token);
        var results = await Task.WhenAll(first, second);
        Assert.All(results, x => Assert.Null(x.Error));
        var creator = Assert.Single(results, x => x.Result!.CreatedPipelineIds.Count > 0);
        Assert.Equal(creator.Result!.Pipelines.Skip(upgrade ? 2 : 0).Select(p => p.Id), creator.Result.CreatedPipelineIds);
        Assert.Single(results, x => x.Result!.CreatedPipelineIds.Count == 0);
        Assert.Equal(results[0].Result!.Pipelines.Select(p => p.Id), results[1].Result!.Pipelines.Select(p => p.Id));
        Assert.Equal(JsonSerializer.Serialize(results[0].Result!.Pipelines), JsonSerializer.Serialize(results[1].Result!.Pipelines));
        await using var check = CreateContext();
        Assert.Equal(defaultChoice != "none" ? 4 : 3, await check.Pipelines.CountAsync(token));
        Assert.Equal(21, await check.PipelineStages.CountAsync(token));
        Assert.Equal(defaultChoice != "none" ? chosen.Id : upgrade ? (Guid?)null : results[0].Result!.Pipelines[0].Id,
            (await check.Workspaces.SingleAsync(token)).DefaultPipelineId);
    }
}
