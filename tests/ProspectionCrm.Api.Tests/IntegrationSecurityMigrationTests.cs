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

public sealed class IntegrationSecurityMigrationTests : IAsyncLifetime
{
    public const string Current = "20261014075029_Phase932IntegrationConnection";
    private readonly PostgreSqlContainer postgres = new PostgreSqlBuilder("postgres:18").Build();
    public Task InitializeAsync() => postgres.StartAsync();
    public Task DisposeAsync() => postgres.DisposeAsync().AsTask();
    [Fact]
    public async Task UpgradeKeepsUnboundLegacyOperationsClosedAndDowngradePreservesHistoricalData()
    {
        await using var db = new ProspectionCrmDbContext(new DbContextOptionsBuilder<ProspectionCrmDbContext>()
            .UseNpgsql(postgres.GetConnectionString()).Options);
        var assembly = db.GetService<IMigrationsAssembly>();
        var migration = assembly.CreateMigration(assembly.Migrations[Current], db.Database.ProviderName!);
        Assert.Single(migration.UpOperations.OfType<AddColumnOperation>());
        Assert.All(migration.UpOperations, op => Assert.True(op is AddColumnOperation or AddCheckConstraintOperation));
        await db.GetService<IMigrator>().MigrateAsync(IntegrationPersistenceMigrationTests.Current);
        var workspace = new Workspace { Name = "Legacy", TimeZoneId = "UTC", OwnerUser = new() { Email = "legacy@example.invalid" } };
        db.Add(workspace); await db.SaveChangesAsync();
        var id = Guid.NewGuid(); var command = Guid.NewGuid(); var correlation = Guid.NewGuid(); var probe = Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "IntegrationOperations" ("Id","WorkspaceId","CommandMessageId","CorrelationId","ProbeId","Version","Type","StatusCode","Revision","CreatedAt")
            VALUES ({id},{workspace.Id},{command},{correlation},{probe},1,'technical.probe','awaiting-result',1,now())
            """);
        var before = await Snapshot(db);
        await db.Database.MigrateAsync();
        Assert.Null((await db.IntegrationOperations.SingleAsync()).ConnectionId);
        var json = ProspectionCrm.Api.Dtos.Integrations.IntegrationContracts.Serialize(new ProspectionCrm.Api.Dtos.Integrations.IntegrationResultEnvelope(
            1, Guid.NewGuid(), id, correlation, command, "technical.probe", new(probe, "succeeded")));
        await Assert.ThrowsAsync<IntegrationAccessException>(() => new IntegrationStore(db, TimeProvider.System)
            .ReceiveAsync(new(workspace.Id, Guid.NewGuid()), json));
        Assert.False(db.Database.HasPendingModelChanges()); Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.Equal(CalendarEventPlanMigrationTests.Current, (await db.Database.GetAppliedMigrationsAsync()).Last());
        await db.GetService<IMigrator>().MigrateAsync(IntegrationPersistenceMigrationTests.Current);
        Assert.Equal(before, await Snapshot(db));
        await db.Database.MigrateAsync();
        Assert.Null((await db.IntegrationOperations.AsNoTracking().SingleAsync()).ConnectionId);
        Assert.Equal(before, await Snapshot(db));
    }
    private static Task<string[]> Snapshot(ProspectionCrmDbContext db) => db.Database.SqlQuery<string>($"""
        SELECT (to_jsonb(o) - 'ConnectionId')::text AS "Value" FROM "IntegrationOperations" o
        UNION ALL SELECT to_jsonb(w)::text FROM "Workspaces" w
        UNION ALL SELECT to_jsonb(u)::text FROM "UserAccounts" u ORDER BY "Value"
        """).ToArrayAsync();
}
