using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using ProspectionCrm.Api.Data;
using ProspectionCrm.Api.Dtos.Setup;
using ProspectionCrm.Api.Entities;
using ProspectionCrm.Api.Services;
using Testcontainers.PostgreSql;
using Xunit;

namespace ProspectionCrm.Api.Tests;

// One disposable PostgreSQL instance per test: never read development connection settings.
public sealed class BootstrapTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer postgres = new PostgreSqlBuilder("postgres:18").Build();

    public async Task InitializeAsync()
    {
        await postgres.StartAsync();
        await using var db = CreateContext();
        await db.Database.MigrateAsync();
    }

    public Task DisposeAsync() => postgres.DisposeAsync().AsTask();

    private ProspectionCrmDbContext CreateContext() => new(
        new DbContextOptionsBuilder<ProspectionCrmDbContext>()
            .UseNpgsql(postgres.GetConnectionString()).Options);

    [Fact]
    public async Task HttpBootstrapCreatesOnlyOwnerAndWorkspaceAndIsIdempotent()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.UseEnvironment("Testing")
                .UseSetting("ConnectionStrings:DefaultConnection", postgres.GetConnectionString()));
        using var client = factory.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
        await using var db = CreateContext();
        // Starting the actual API must not implicitly initialize the business data.
        Assert.Empty(await db.UserAccounts.ToListAsync());
        Assert.Empty(await db.Workspaces.ToListAsync());

        using var first = await client.PostAsync("/api/setup/bootstrap", null);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var created = await first.Content.ReadFromJsonAsync<BootstrapDto>();
        Assert.NotNull(created);
        var owner = Assert.Single(await db.UserAccounts.AsNoTracking().ToListAsync());
        var workspace = Assert.Single(await db.Workspaces.AsNoTracking().ToListAsync());
        Assert.NotEqual(Guid.Empty, owner.Id);
        Assert.NotEqual(Guid.Empty, workspace.Id);
        Assert.Equal(owner.Id, workspace.OwnerUserId);
        Assert.Null(workspace.ArchivedAt);
        Assert.Equal(new BootstrapDto(owner.Id, BootstrapDefaults.OwnerEmail,
            BootstrapDefaults.OwnerDisplayName, workspace.Id, BootstrapDefaults.WorkspaceName,
            BootstrapDefaults.TimeZoneId), created);
        Assert.Equal(workspace.Id, await new CurrentWorkspaceProvider(db).GetCurrentWorkspaceIdAsync());

        using var second = await client.PostAsync("/api/setup/bootstrap", null);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(created, await second.Content.ReadFromJsonAsync<BootstrapDto>());
        Assert.Equal(1, await db.UserAccounts.CountAsync());
        Assert.Equal(1, await db.Workspaces.CountAsync());
        Assert.Empty(await db.Pipelines.ToListAsync());
        Assert.Empty(await db.PipelineStages.ToListAsync());
        Assert.Empty(await db.Opportunities.ToListAsync());
    }

    [Fact]
    public async Task CompatibleInstallationKeepsCustomValuesAndTimestamps()
    {
        await using var db = CreateContext();
        var owner = new UserAccount { Email = "custom@example.invalid", DisplayName = "Custom owner" };
        var workspace = new Workspace { OwnerUser = owner, Name = "Custom workspace", TimeZoneId = "UTC" };
        db.Workspaces.Add(workspace);
        await db.SaveChangesAsync();
        var originalOwner = await db.UserAccounts.AsNoTracking().SingleAsync();
        var originalWorkspace = await db.Workspaces.AsNoTracking().SingleAsync();

        var result = await new BootstrapService(db).BootstrapAsync(default);
        Assert.Null(result.Error);
        Assert.False(result.Created);
        Assert.Equal(new BootstrapDto(owner.Id, owner.Email, owner.DisplayName,
            workspace.Id, workspace.Name, workspace.TimeZoneId), result.Bootstrap);
        Assert.Equal(originalOwner.CreatedAt, (await db.UserAccounts.AsNoTracking().SingleAsync()).CreatedAt);
        var persisted = await db.Workspaces.AsNoTracking().SingleAsync();
        Assert.Equal(originalWorkspace.CreatedAt, persisted.CreatedAt);
        Assert.Null(persisted.UpdatedAt);
    }

    [Theory]
    [InlineData("owner-only")]
    [InlineData("multiple-owners")]
    [InlineData("archived-workspace")]
    [InlineData("multiple-active-workspaces")]
    [InlineData("active-and-archived-workspaces")]
    [InlineData("workspace-only")]
    [InlineData("wrong-owner")]
    public async Task HttpBootstrapRejectsIncompatibleStateWithoutChangingIt(string state)
    {
        await using var db = CreateContext();
        var owner = new UserAccount { Email = BootstrapDefaults.OwnerEmail };
        if (state != "workspace-only")
            db.UserAccounts.Add(owner);
        if (state is "workspace-only" or "wrong-owner")
        {
            // Simulate legacy corruption that the real FK normally prevents, only in this container.
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE \"Workspaces\" DROP CONSTRAINT \"FK_Workspaces_UserAccounts_OwnerUserId\"");
            db.Workspaces.Add(new Workspace
            {
                OwnerUserId = Guid.NewGuid(), Name = "Orphan", TimeZoneId = "UTC"
            });
        }
        else if (state != "owner-only")
            db.Workspaces.Add(new Workspace
            {
                OwnerUser = owner, Name = "Existing", TimeZoneId = "UTC",
                ArchivedAt = state == "archived-workspace" ? DateTimeOffset.UtcNow : null
            });
        if (state == "multiple-owners")
            db.UserAccounts.Add(new UserAccount { Email = "other@example.invalid" });
        if (state is "multiple-active-workspaces" or "active-and-archived-workspaces")
            db.Workspaces.Add(new Workspace
            {
                OwnerUser = owner, Name = "Other", TimeZoneId = "UTC",
                ArchivedAt = state == "active-and-archived-workspaces" ? DateTimeOffset.UtcNow : null
            });
        await db.SaveChangesAsync();
        var before = await SnapshotAsync(db);
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.UseEnvironment("Testing")
                .UseSetting("ConnectionStrings:DefaultConnection", postgres.GetConnectionString()));
        using var client = factory.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
        using var response = await client.PostAsync("/api/setup/bootstrap", null);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.NotNull(problem);
        Assert.Equal(409, problem.Status);
        Assert.Equal("Incompatible V1 setup state", problem.Title);
        Assert.Equal(before, await SnapshotAsync(db));
    }

    [Fact]
    public async Task FailedWorkspaceInsertRollsBackOwnerAndReleasesLock()
    {
        await using var db = CreateContext();
        await db.Database.ExecuteSqlRawAsync(
            "ALTER TABLE \"Workspaces\" ADD CONSTRAINT reject_bootstrap CHECK (false)");
        await Assert.ThrowsAsync<DbUpdateException>(() => new BootstrapService(db).BootstrapAsync(default));
        await using var check = CreateContext();
        Assert.Empty(await check.UserAccounts.ToListAsync());
        Assert.Empty(await check.Workspaces.ToListAsync());
        await check.Database.ExecuteSqlRawAsync("ALTER TABLE \"Workspaces\" DROP CONSTRAINT reject_bootstrap");
        Assert.True((await new BootstrapService(check).BootstrapAsync(default)).Created);
    }

    [Fact]
    public async Task ConcurrentBootstrapsWaitForLockAndReturnTheSameInstallation()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var token = timeout.Token;
        await using var blocker = CreateContext();
        await using var transaction = await blocker.Database.BeginTransactionAsync(token);
        await blocker.Database.ExecuteSqlRawAsync(
            "LOCK TABLE \"UserAccounts\", \"Workspaces\" IN SHARE ROW EXCLUSIVE MODE", token);
        await using var firstDb = CreateContext();
        await using var secondDb = CreateContext();
        var first = new BootstrapService(firstDb).BootstrapAsync(token);
        var second = new BootstrapService(secondDb).BootstrapAsync(token);

        // Observe both independent connections actually waiting in PostgreSQL before releasing them.
        await using var observer = new NpgsqlConnection(postgres.GetConnectionString());
        await observer.OpenAsync(token);
        await using var waiting = new NpgsqlCommand(
            "SELECT count(*) FROM pg_locks WHERE relation = '\"UserAccounts\"'::regclass AND NOT granted", observer);
        while (Convert.ToInt64(await waiting.ExecuteScalarAsync(token)) < 2)
            await Task.Delay(25, token);
        Assert.False(first.IsCompleted);
        Assert.False(second.IsCompleted);
        await transaction.CommitAsync(token);

        var results = await Task.WhenAll(first, second);
        Assert.All(results, result => Assert.Null(result.Error));
        Assert.Single(results, result => result.Created);
        Assert.Equal(results[0].Bootstrap, results[1].Bootstrap);
        await using var check = CreateContext();
        Assert.Equal(1, await check.UserAccounts.CountAsync(token));
        Assert.Equal(1, await check.Workspaces.CountAsync(token));
        Assert.Empty(await check.Pipelines.ToListAsync(token));
        Assert.Empty(await check.PipelineStages.ToListAsync(token));
        Assert.Empty(await check.Opportunities.ToListAsync(token));
    }

    private static async Task<string> SnapshotAsync(ProspectionCrmDbContext db)
        => System.Text.Json.JsonSerializer.Serialize(new
        {
            Owners = await db.UserAccounts.AsNoTracking().OrderBy(x => x.Id).ToListAsync(),
            Workspaces = await db.Workspaces.AsNoTracking().OrderBy(x => x.Id).ToListAsync()
        });
}
