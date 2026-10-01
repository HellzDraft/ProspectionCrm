using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using ProspectionCrm.Api.Data;
using ProspectionCrm.Api.Dtos.Opportunities;
using ProspectionCrm.Api.Dtos.Pipelines;
using ProspectionCrm.Api.Dtos.Setup;
using ProspectionCrm.Api.Entities;
using ProspectionCrm.Api.Services;
using Testcontainers.PostgreSql;
using Xunit;

namespace ProspectionCrm.Api.Tests;

// A complete HTTP journey against a fresh PostgreSQL instance, never the development database.
public sealed class Phase4ReconstructionTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer postgres = new PostgreSqlBuilder("postgres:18").Build();
    public Task InitializeAsync() => postgres.StartAsync();
    public Task DisposeAsync() => postgres.DisposeAsync().AsTask();
    private ProspectionCrmDbContext CreateContext() => new(
        new DbContextOptionsBuilder<ProspectionCrmDbContext>().UseNpgsql(postgres.GetConnectionString()).Options);

    private static async Task<T> CreatedAsync<T>(HttpClient client, string route, object body)
    {
        using var response = await client.PostAsJsonAsync(route, body);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.NotNull(response.Headers.Location);
        using var detail = await client.GetAsync(response.Headers.Location);
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private static async Task PostStatusAsync(HttpClient client, string route, HttpStatusCode expected)
    {
        using var response = await client.PostAsync(route, null);
        Assert.Equal(expected, response.StatusCode);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task OpportunitySelectionAndArchiveCannotInterleave(bool archiveParent, bool reassign)
    {
        await using var setup = CreateContext();
        await setup.Database.MigrateAsync();
        var workspaceId = (await new BootstrapService(setup).BootstrapAsync(default)).Bootstrap!.WorkspaceId;
        var pipeline = new Pipeline { WorkspaceId = workspaceId, Name = "Concurrent", TypeCode = "custom" };
        var target = new PipelineStage { Pipeline = pipeline, Name = "Target", CategoryCode = "active", SortOrder = 0 };
        var other = new PipelineStage { Pipeline = pipeline, Name = "Other", CategoryCode = "active", SortOrder = 1 };
        setup.PipelineStages.AddRange(target, other);
        var existing = new Opportunity { WorkspaceId = workspaceId, PipelineStage = other, Title = "Existing", PriorityCode = "normal" };
        if (reassign) setup.Opportunities.Add(existing);
        await setup.SaveChangesAsync();
        (await setup.Workspaces.SingleAsync()).DefaultPipelineId = pipeline.Id;
        await setup.SaveChangesAsync();

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var token = timeout.Token;
        var gate = new BeforeSaveGate();
        await using var writer = new ProspectionCrmDbContext(new DbContextOptionsBuilder<ProspectionCrmDbContext>()
            .UseNpgsql(postgres.GetConnectionString()).AddInterceptors(gate).Options);
        var opportunities = new OpportunityService(writer, new CurrentWorkspaceProvider(writer));
        async Task WriteAsync()
        {
            if (reassign)
            {
                var result = await opportunities.UpdateAsync(existing.Id,
                    new() { Title = "Selected", PipelineStageId = target.Id, PriorityCode = "normal" }, token);
                Assert.True(result.Found);
                Assert.Null(result.Error);
            }
            else
            {
                var result = await opportunities.CreateAsync(
                    new() { Title = "Selected", PipelineStageId = target.Id, PriorityCode = "normal" }, token);
                Assert.NotNull(result.Opportunity);
                Assert.Null(result.Error);
            }
        }
        var write = WriteAsync();
        await gate.Reached.Task.WaitAsync(token); // References validated; opportunity INSERT/UPDATE has not happened.
        await using var archiver = CreateContext();
        async Task ArchiveAsync()
        {
            if (archiveParent)
                Assert.True(await new PipelineService(archiver, new CurrentWorkspaceProvider(archiver)).ArchiveAsync(pipeline.Id, token));
            else
                Assert.Equal(PipelineStageWriteStatus.Succeeded,
                    (await new PipelineStageService(archiver, new CurrentWorkspaceProvider(archiver)).ArchiveAsync(pipeline.Id, target.Id, token)).Status);
        }
        var archive = ArchiveAsync();
        var blocked = false;
        try
        {
            await using var observer = new NpgsqlConnection(postgres.GetConnectionString());
            await observer.OpenAsync(token);
            await using var command = new NpgsqlCommand(
                "SELECT count(*) FROM pg_stat_activity WHERE datname = current_database() AND wait_event_type = 'Lock' AND query LIKE '%Pipelines%'", observer);
            while (!archive.IsCompleted)
            {
                if (Convert.ToInt64(await command.ExecuteScalarAsync(token)) > 0) { blocked = true; break; }
                await Task.Delay(25, token);
            }
        }
        finally
        {
            gate.Release.TrySetResult();
        }
        await Task.WhenAll(write, archive);
        Assert.True(blocked, "Archive committed between reference validation and the opportunity write.");
        await using var check = CreateContext();
        Assert.Equal(target.Id, (await check.Opportunities.SingleAsync(token)).PipelineStageId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WaitingOpportunitySelectionRechecksCommittedArchive(bool archiveParent)
    {
        await using var setup = CreateContext();
        await setup.Database.MigrateAsync();
        var workspaceId = (await new BootstrapService(setup).BootstrapAsync(default)).Bootstrap!.WorkspaceId;
        var pipeline = new Pipeline { WorkspaceId = workspaceId, Name = "Target", TypeCode = "custom" };
        var stage = new PipelineStage { Pipeline = pipeline, Name = "Target", CategoryCode = "active", SortOrder = 0 };
        setup.PipelineStages.Add(stage);
        await setup.SaveChangesAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var token = timeout.Token;
        await using var blocker = CreateContext();
        await using var transaction = await blocker.Database.BeginTransactionAsync(token);
        await blocker.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"Pipelines\" WHERE \"Id\" = {pipeline.Id} FOR UPDATE", token);
        if (archiveParent)
            await blocker.Pipelines.Where(x => x.Id == pipeline.Id).ExecuteUpdateAsync(x => x.SetProperty(p => p.ArchivedAt, DateTimeOffset.UtcNow), token);
        else
            await blocker.PipelineStages.Where(x => x.Id == stage.Id).ExecuteUpdateAsync(x => x.SetProperty(s => s.ArchivedAt, DateTimeOffset.UtcNow), token);
        await using var writer = CreateContext();
        var create = new OpportunityService(writer, new CurrentWorkspaceProvider(writer)).CreateAsync(
            new() { Title = "Rejected", PipelineStageId = stage.Id, PriorityCode = "normal" }, token);
        await using var observer = new NpgsqlConnection(postgres.GetConnectionString());
        await observer.OpenAsync(token);
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM pg_stat_activity WHERE datname = current_database() AND wait_event_type = 'Lock' AND query LIKE '%Pipelines%'", observer);
        while (Convert.ToInt64(await command.ExecuteScalarAsync(token)) == 0)
        {
            Assert.False(create.IsCompleted);
            await Task.Delay(25, token);
        }
        await transaction.CommitAsync(token);
        var result = await create;
        Assert.Null(result.Opportunity);
        Assert.NotNull(result.Error);
        Assert.Empty(await writer.Opportunities.AsNoTracking().ToArrayAsync(token));
    }

    [Fact]
    public async Task OpportunitySelectionsShareTheLockAndDoNotBlockOtherPipelines()
    {
        await using var setup = CreateContext();
        await setup.Database.MigrateAsync();
        var workspaceId = (await new BootstrapService(setup).BootstrapAsync(default)).Bootstrap!.WorkspaceId;
        var stages = new[] { "First", "Other" }.Select(name => new PipelineStage
        {
            Name = name, CategoryCode = "active", SortOrder = 0,
            Pipeline = new Pipeline { WorkspaceId = workspaceId, Name = name, TypeCode = "custom" }
        }).ToArray();
        setup.PipelineStages.AddRange(stages);
        await setup.SaveChangesAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var token = timeout.Token;
        var gate = new BeforeSaveGate();
        await using var writer = new ProspectionCrmDbContext(new DbContextOptionsBuilder<ProspectionCrmDbContext>()
            .UseNpgsql(postgres.GetConnectionString()).AddInterceptors(gate).Options);
        var first = new OpportunityService(writer, new CurrentWorkspaceProvider(writer)).CreateAsync(
            new() { Title = "Paused", PipelineStageId = stages[0].Id, PriorityCode = "normal" }, token);
        await gate.Reached.Task.WaitAsync(token);
        try
        {
            await using var other = CreateContext();
            var selected = await new OpportunityService(other, new CurrentWorkspaceProvider(other)).CreateAsync(
                new() { Title = "Concurrent", PipelineStageId = stages[0].Id, PriorityCode = "normal" }, token);
            Assert.Null(selected.Error);
            var written = await new PipelineStageService(other, new CurrentWorkspaceProvider(other)).CreateAsync(
                stages[1].PipelineId, new() { Name = "Independent", CategoryCode = "active" }, token);
            Assert.Equal(PipelineStageWriteStatus.Succeeded, written.Status);
            Assert.False(first.IsCompleted);
        }
        finally { gate.Release.TrySetResult(); }
        Assert.Null((await first).Error);
    }

    private sealed class BeforeSaveGate : SaveChangesInterceptor
    {
        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Reached.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return result;
        }
    }

    [Fact]
    public async Task FreshDatabaseSupportsTheCompletePhase4Journey()
    {
        await using var db = CreateContext();
        await using (var connection = new NpgsqlConnection(postgres.GetConnectionString()))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand("SELECT count(*) FROM pg_tables WHERE schemaname = 'public'", connection);
            Assert.Equal(0L, await command.ExecuteScalarAsync());
        }
        Assert.Empty(await db.Database.GetAppliedMigrationsAsync());
        Assert.False(db.Database.HasPendingModelChanges());
        await db.Database.MigrateAsync();
        Assert.Equal(db.Database.GetMigrations(), await db.Database.GetAppliedMigrationsAsync());
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());

        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder
            .UseEnvironment("Testing").UseSetting("ConnectionStrings:DefaultConnection", postgres.GetConnectionString()));
        using var client = factory.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
        Assert.Empty(await db.UserAccounts.ToArrayAsync());
        Assert.Empty(await db.Workspaces.ToArrayAsync());
        using var bootstrap = await client.PostAsync("/api/setup/bootstrap", null);
        Assert.Equal(HttpStatusCode.Created, bootstrap.StatusCode);
        var setup = (await bootstrap.Content.ReadFromJsonAsync<BootstrapDto>())!;
        Assert.Equal(setup.OwnerUserId, Assert.Single(await db.UserAccounts.AsNoTracking().ToArrayAsync()).Id);
        var workspace = Assert.Single(await db.Workspaces.AsNoTracking().ToArrayAsync());
        Assert.Equal(setup.WorkspaceId, workspace.Id);
        Assert.Equal(setup.OwnerUserId, workspace.OwnerUserId);
        Assert.Null(workspace.ArchivedAt);
        Assert.Empty(await db.Pipelines.ToArrayAsync());
        Assert.Empty(await db.PipelineStages.ToArrayAsync());
        Assert.Empty(await db.Opportunities.ToArrayAsync());
        using var repeated = await client.PostAsync("/api/setup/bootstrap", null);
        Assert.Equal(HttpStatusCode.OK, repeated.StatusCode);
        Assert.Equal(setup, await repeated.Content.ReadFromJsonAsync<BootstrapDto>());

        var pipeline = await CreatedAsync<PipelineDto>(client, "/api/pipelines",
            new { name = "Audit only", typeCode = "custom", isVisible = false });
        var route = $"/api/pipelines/{pipeline.Id}";
        var first = await CreatedAsync<PipelineStageDto>(client, $"{route}/stages", new { name = "First", categoryCode = "active" });
        var archived = await CreatedAsync<PipelineStageDto>(client, $"{route}/stages", new { name = "Archived", categoryCode = "failure" });
        await PostStatusAsync(client, $"{route}/stages/{archived.Id}/archive", HttpStatusCode.NoContent);
        var last = await CreatedAsync<PipelineStageDto>(client, $"{route}/stages", new { name = "Last", categoryCode = "success" });
        Assert.Equal(2, last.SortOrder); // Archived slot 1 remains occupied.
        using (var order = await client.PutAsJsonAsync($"{route}/stages/order", new { stageIds = new[] { last.Id, first.Id } }))
            Assert.Equal(HttpStatusCode.NoContent, order.StatusCode);
        var ordered = (await client.GetFromJsonAsync<PipelineDto>($"{route}?includeArchivedStages=true"))!;
        Assert.Equal(new[] { last.Id, archived.Id, first.Id }, ordered.Stages.Select(x => x.Id));
        Assert.Equal(new[] { 0, 1, 2 }, ordered.Stages.Select(x => x.SortOrder));
        Assert.NotNull(ordered.Stages[1].ArchivedAt);
        await PostStatusAsync(client, $"{route}/set-default", HttpStatusCode.NoContent);
        Assert.Equal(pipeline.Id, (await db.Workspaces.AsNoTracking().SingleAsync()).DefaultPipelineId);

        var opportunityBody = new { title = "Historical opportunity", pipelineStageId = first.Id, priorityCode = "normal" };
        var opportunity = await CreatedAsync<OpportunityDto>(client, "/api/opportunities", opportunityBody);
        var historical = JsonSerializer.Serialize(await db.Opportunities.AsNoTracking().SingleAsync());
        await PostStatusAsync(client, $"{route}/stages/{first.Id}/archive", HttpStatusCode.NoContent);
        using (var rejected = await client.PostAsJsonAsync("/api/opportunities", opportunityBody))
            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        await PostStatusAsync(client, $"{route}/stages/{first.Id}/restore", HttpStatusCode.NoContent);
        await PostStatusAsync(client, $"{route}/archive", HttpStatusCode.NoContent);
        Assert.Null((await db.Workspaces.AsNoTracking().SingleAsync()).DefaultPipelineId);
        await PostStatusAsync(client, $"{route}/set-default", HttpStatusCode.Conflict);
        using (var rejected = await client.PostAsJsonAsync("/api/opportunities", opportunityBody))
            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        using (var rejected = await client.PostAsJsonAsync($"{route}/stages", new { name = "Blocked", categoryCode = "active" }))
            Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
        Assert.Equal(historical, JsonSerializer.Serialize(await db.Opportunities.AsNoTracking().SingleAsync()));

        var clone = await CreatedAsync<PipelineDto>(client, $"{route}/clone", new { name = "Audit clone" });
        var exported = (await client.GetFromJsonAsync<JsonObject>($"{route}/export"))!;
        Assert.Equal(1, exported["schemaVersion"]!.GetValue<int>());
        var imported = await CreatedAsync<PipelineDto>(client, "/api/pipelines/import", exported);
        var reexported = await client.GetFromJsonAsync<JsonObject>($"/api/pipelines/{imported.Id}/export");
        Assert.True(JsonNode.DeepEquals(exported, reexported));
        Assert.Equal(3, new[] { pipeline.Id, clone.Id, imported.Id }.Distinct().Count());
        foreach (var created in new[] { clone, imported })
        {
            Assert.Null(created.ArchivedAt);
            Assert.Null(created.PreferredCandidateProfileId);
            Assert.False(created.IsDefault);
            Assert.False(created.IsVisible);
            Assert.Equal(new[] { "Last", "First" }, created.Stages.Select(x => x.Name));
            Assert.Equal(new[] { 0, 1 }, created.Stages.Select(x => x.SortOrder));
            Assert.All(created.Stages, x => Assert.Null(x.ArchivedAt));
            Assert.Equal(setup.WorkspaceId, (await db.Pipelines.AsNoTracking().SingleAsync(x => x.Id == created.Id)).WorkspaceId);
        }
        Assert.Equal(7, (await db.PipelineStages.Select(x => x.Id).ToArrayAsync()).Distinct().Count());
        Assert.Equal(historical, JsonSerializer.Serialize(await db.Opportunities.AsNoTracking().SingleAsync()));
        Assert.Equal(first.Id, (await client.GetFromJsonAsync<OpportunityDto>($"/api/opportunities/{opportunity.Id}"))!.PipelineStageId);
        await PostStatusAsync(client, $"{route}/restore", HttpStatusCode.NoContent);
        Assert.Null((await db.Workspaces.AsNoTracking().SingleAsync()).DefaultPipelineId);
        Assert.Null((await client.GetFromJsonAsync<PipelineDto>(route))!.ArchivedAt);

        // An archived second workspace keeps the V1 current-workspace selection unambiguous.
        var foreign = new Pipeline
        {
            Name = "Foreign", TypeCode = "custom", Workspace = new Workspace
            {
                OwnerUserId = setup.OwnerUserId, Name = "Other", TimeZoneId = "UTC", ArchivedAt = DateTimeOffset.UtcNow
            },
            Stages = new List<PipelineStage> { new() { Name = "Foreign stage", CategoryCode = "active", SortOrder = 0 } }
        };
        db.Pipelines.Add(foreign);
        await db.SaveChangesAsync();
        foreach (var suffix in new[] { "", "/stages", "/export" })
        {
            using var rejected = await client.GetAsync($"/api/pipelines/{foreign.Id}{suffix}");
            Assert.Equal(HttpStatusCode.NotFound, rejected.StatusCode);
        }
        using (var rejected = await client.PostAsJsonAsync($"/api/pipelines/{foreign.Id}/clone", new { name = "Rejected" }))
            Assert.Equal(HttpStatusCode.NotFound, rejected.StatusCode);
        using (var rejected = await client.GetAsync($"{route}/stages/{foreign.Stages.Single().Id}"))
            Assert.Equal(HttpStatusCode.NotFound, rejected.StatusCode);
        using (var rejected = await client.PostAsJsonAsync("/api/opportunities",
            new { title = "Rejected", pipelineStageId = foreign.Stages.Single().Id, priorityCode = "normal" }))
            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        var listed = (await client.GetFromJsonAsync<PipelineDto[]>("/api/pipelines"))!;
        Assert.Equal(3, listed.Length);
        Assert.DoesNotContain(listed, x => x.Id == foreign.Id);
        Assert.Equal(historical, JsonSerializer.Serialize(await db.Opportunities.AsNoTracking().SingleAsync()));
        Assert.Equal(4, await db.Pipelines.CountAsync());
        Assert.Equal(8, await db.PipelineStages.CountAsync());
        Assert.False(db.Database.HasPendingModelChanges());
    }
}
