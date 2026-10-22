using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using ProspectionCrm.Api.Data;
using ProspectionCrm.Api.Entities;
using ProspectionCrm.Api.Services.Integrations;
using Testcontainers.PostgreSql;
using Xunit;

namespace ProspectionCrm.Api.Tests;

public sealed class IntegrationPersistenceMigrationTests : IAsyncLifetime
{
    public const string Current = "20261013143800_Phase931IntegrationPersistence";
    private readonly PostgreSqlContainer postgres = new PostgreSqlBuilder("postgres:18").Build();
    public Task InitializeAsync() => postgres.StartAsync();
    public Task DisposeAsync() => postgres.DisposeAsync().AsTask();
    private ProspectionCrmDbContext Db() => new(new DbContextOptionsBuilder<ProspectionCrmDbContext>()
        .UseNpgsql(postgres.GetConnectionString()).Options);

    [Fact]
    public async Task AdditiveMigrationSupportsFreshDatabaseAndPopulatedUpgradeWithoutChangingBusinessRows()
    {
        await using var db = Db();
        var assembly = db.GetService<IMigrationsAssembly>();
        var migration = assembly.CreateMigration(assembly.Migrations[Current], db.Database.ProviderName!);
        Assert.All(migration.UpOperations, operation => Assert.True(operation is CreateTableOperation or CreateIndexOperation));
        Assert.Equal(new[] { "IntegrationAttempts", "IntegrationOperations", "IntegrationReceipts" },
            migration.UpOperations.OfType<CreateTableOperation>().Select(x => x.Name).Order().ToArray());
        await db.Database.MigrateAsync();
        Assert.False(db.Database.HasPendingModelChanges()); Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.Equal(CalendarEventDeliveryMigrationTests.Current, (await db.Database.GetAppliedMigrationsAsync()).Last());
        await db.GetService<IMigrator>().MigrateAsync(AutomationRuntimeMigrationTests.Current);
        var company = new Company { Name = "Preserved before 9.3.1", Workspace = new Workspace { Name = "Migration",
            TimeZoneId = "UTC", OwnerUser = new() { Email = "integration-migration@example.invalid" } } };
        db.Add(company); await db.SaveChangesAsync();
        var before = await Snapshot(db);
        await db.Database.MigrateAsync();
        Assert.Equal(before, await Snapshot(db));
        Assert.Empty(await db.IntegrationOperations.ToListAsync()); Assert.Empty(await db.IntegrationAttempts.ToListAsync());
        Assert.Empty(await db.IntegrationReceipts.ToListAsync());
        var store = new IntegrationStore(db, TimeProvider.System);
        var op = await store.CreateProbeAsync(company.WorkspaceId);
        await store.BeginAttemptAsync(company.WorkspaceId, op.Id, 0);
        await db.GetService<IMigrator>().MigrateAsync(AutomationRuntimeMigrationTests.Current);
        Assert.Equal(before, await Snapshot(db));
        await db.Database.MigrateAsync();
        Assert.Equal(before, await Snapshot(db)); Assert.Empty(await db.IntegrationOperations.ToListAsync());
        Assert.False(db.Database.HasPendingModelChanges()); Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.Equal(db.Database.GetMigrations(), await db.Database.GetAppliedMigrationsAsync());
    }

    private static Task<string[]> Snapshot(ProspectionCrmDbContext db) => db.Database.SqlQuery<string>($"""
        SELECT to_jsonb(c)::text AS "Value" FROM "Companies" c
        UNION ALL SELECT to_jsonb(w)::text FROM "Workspaces" w
        UNION ALL SELECT to_jsonb(u)::text FROM "UserAccounts" u
        ORDER BY "Value"
        """).ToArrayAsync();
}
