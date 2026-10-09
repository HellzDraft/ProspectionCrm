using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using ProspectionCrm.Api.Data;
using ProspectionCrm.Api.Entities;
using Testcontainers.PostgreSql;
using Xunit;

namespace ProspectionCrm.Api.Tests;

public sealed class AutomationJobMigrationTests : IAsyncLifetime
{
    private const string Previous = "20261007124044_Phase81AutomationRuntimeSettings";
    public const string Current = "20261008083956_Phase82AutomationJobs";
    private readonly PostgreSqlContainer postgres = new PostgreSqlBuilder("postgres:18").Build();
    public Task InitializeAsync() => postgres.StartAsync();
    public Task DisposeAsync() => postgres.DisposeAsync().AsTask();
    private ProspectionCrmDbContext Db() => new(new DbContextOptionsBuilder<ProspectionCrmDbContext>().UseNpgsql(postgres.GetConnectionString()).Options);

    [Fact]
    public async Task FreshDatabaseReconstructsFullModelWithoutPendingMigrations()
    {
        await using var db = Db(); Assert.Empty(await db.Database.GetAppliedMigrationsAsync());
        await db.Database.MigrateAsync(); await AssertCurrent(db); Assert.Empty(await db.AutomationJobs.ToListAsync());
    }

    [Fact]
    public async Task UpgradeFromPhase81AndDownUpPreserveRulesSettingsAndExecutionHistory()
    {
        await using var db = Db(); await db.GetService<IMigrator>().MigrateAsync(Previous);
        var workspace = new Workspace { Name = "Existing", TimeZoneId = "UTC", OwnerUser = new UserAccount { Email = "migration@example.invalid" },
            AutomationRuntimeSettings = new() { IsEnabled = true, OperatingModeCode = "assist" } };
        var rule = new AutomationRule { Workspace = workspace, Name = "Existing rule", TriggerTypeCode = "manual", ActionTypeCode = "test", ConditionJson = "{}" };
        var execution = new AutomationExecution { Workspace = workspace, AutomationRule = rule, StatusCode = "pending", TriggeredAt = DateTimeOffset.UtcNow };
        db.Add(rule); await db.SaveChangesAsync();
        // The current EF model has 8.4 columns that do not exist in the old schema yet.
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "AutomationExecutions" ("Id", "WorkspaceId", "AutomationRuleId", "StatusCode", "TriggeredAt")
            VALUES ({execution.Id}, {workspace.Id}, {rule.Id}, 'pending', {execution.TriggeredAt})
            """);
        db.ChangeTracker.Clear();
        var before = await Snapshot(db);
        await db.Database.MigrateAsync(); await AssertCurrent(db); Assert.Equal(before, await Snapshot(db));
        Assert.Empty(await db.AutomationJobs.ToListAsync());
        db.Add(new AutomationJob { WorkspaceId = workspace.Id, AutomationRuleId = rule.Id, TriggerTypeCode = "manual", ActionCategoryCode = "general" });
        await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        await db.GetService<IMigrator>().MigrateAsync(Previous);
        Assert.Equal(Previous, (await db.Database.GetAppliedMigrationsAsync()).Last()); Assert.Equal(before, await Snapshot(db));
        Assert.Equal(0, await db.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM pg_tables WHERE tablename = 'AutomationJobs'").SingleAsync());
        Assert.Equal(0, await db.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM pg_constraint WHERE conname = 'AK_AutomationRules_WorkspaceId_Id'").SingleAsync());
        await db.Database.MigrateAsync(); await AssertCurrent(db); Assert.Equal(before, await Snapshot(db)); Assert.Empty(await db.AutomationJobs.ToListAsync());
    }

    private static async Task AssertCurrent(ProspectionCrmDbContext db)
    {
        Assert.Equal(AutomationRuntimeMigrationTests.Current, db.Database.GetMigrations().Last());
        Assert.Equal(db.Database.GetMigrations(), await db.Database.GetAppliedMigrationsAsync());
        Assert.Empty(await db.Database.GetPendingMigrationsAsync()); Assert.False(db.Database.HasPendingModelChanges());
    }
    private static async Task<string[]> Snapshot(ProspectionCrmDbContext db) => await db.Database.SqlQuery<string>($"""
        SELECT to_jsonb(w)::text AS "Value" FROM "Workspaces" w
        UNION ALL SELECT to_jsonb(r)::text FROM "AutomationRules" r
        UNION ALL SELECT (to_jsonb(e) - ARRAY['AutomationJobId','AttemptNumber','ActionTypeCode','ReasonCode',
            'IsAutomaticAttempt','IsDeferred','EffectApplied','OutcomeSequence','AutomationActionRequestId','IsHumanApprovedAttempt'])::text FROM "AutomationExecutions" e
        UNION ALL SELECT to_jsonb(s)::text FROM "AutomationRuntimeSettings" s
        ORDER BY "Value"
        """).ToArrayAsync();
}
