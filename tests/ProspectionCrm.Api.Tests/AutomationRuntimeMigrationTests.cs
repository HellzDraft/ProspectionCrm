using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using ProspectionCrm.Api.Data;
using ProspectionCrm.Api.Entities;
using Testcontainers.PostgreSql;
using Xunit;

namespace ProspectionCrm.Api.Tests;

public sealed class AutomationRuntimeMigrationTests : IAsyncLifetime
{
    public const string Current = "20261008124357_Phase84AutomationExecutionRuntime";
    private readonly PostgreSqlContainer postgres = new PostgreSqlBuilder("postgres:18").Build();
    public Task InitializeAsync() => postgres.StartAsync();
    public Task DisposeAsync() => postgres.DisposeAsync().AsTask();
    private ProspectionCrmDbContext Db() => new(new DbContextOptionsBuilder<ProspectionCrmDbContext>()
        .UseNpgsql(postgres.GetConnectionString()).Options);

    [Fact]
    public async Task FreshDatabaseHasCurrentModelAndPersistentPartialUniqueness()
    {
        await using var db = Db(); await db.Database.MigrateAsync(); await AssertCurrent(db);
        var indexes = await db.Database.SqlQuery<string>($"""
            SELECT indexdef AS "Value" FROM pg_indexes WHERE indexname IN
            ('UX_CrmTasks_AutomationJob', 'UX_AutomationExecutions_Job_Attempt',
             'UX_AutomationExecutions_Job_Effect', 'UX_AutomationExecutions_Workspace_Outcome')
            """).ToListAsync();
        Assert.Equal(4, indexes.Count);
        Assert.All(indexes, index => { Assert.Contains("UNIQUE", index); Assert.Contains("WHERE", index); });
        Assert.Empty(await db.AutomationExecutions.ToListAsync()); Assert.Empty(await db.CrmTasks.ToListAsync());
    }

    [Fact]
    public async Task UpgradeFromPhase83AndDownUpPreserveLegacyTasksExecutionsAndQueuedJobs()
    {
        await using var db = Db(); await db.GetService<IMigrator>().MigrateAsync(AutomationJobMigrationTests.Current);
        var workspace = new Workspace { Name = "Before 8.4", TimeZoneId = "UTC",
            OwnerUser = new UserAccount { Email = "runtime-migration@example.invalid" }, AutomationRuntimeSettings = new() };
        var rule = new AutomationRule { Workspace = workspace, Name = "Legacy", TriggerTypeCode = "manual", ActionTypeCode = "legacy" };
        var opportunity = new Opportunity { Workspace = workspace, Title = "Existing", PriorityCode = "normal",
            PipelineStage = new() { Name = "Active", CategoryCode = "active",
                Pipeline = new() { Workspace = workspace, Name = "Pipeline", TypeCode = "business" } } };
        var job = new AutomationJob { Workspace = workspace, AutomationRule = rule,
            TriggerTypeCode = "manual", ActionCategoryCode = "general" };
        db.Add(opportunity); db.Add(job); await db.SaveChangesAsync();
        var taskId = Guid.NewGuid(); var executionId = Guid.NewGuid();
        // Use the actual old schema, never the current EF model to insert newly extended entities.
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "CrmTasks" ("Id","OpportunityId","Title","IsCompleted","CreatedAt")
            VALUES ({taskId},{opportunity.Id},'Manual before upgrade',false,now());
            INSERT INTO "AutomationExecutions" ("Id","WorkspaceId","AutomationRuleId","StatusCode","TriggeredAt")
            VALUES ({executionId},{workspace.Id},{rule.Id},'pending',now());
            """);
        db.ChangeTracker.Clear();
        var before = await Snapshot(db);
        await db.Database.MigrateAsync(); await AssertCurrent(db);
        Assert.Equal(before, await Snapshot(db));
        var legacy = await db.AutomationExecutions.AsNoTracking().SingleAsync();
        Assert.Null(legacy.AutomationJobId); Assert.Null(legacy.AttemptNumber); Assert.False(legacy.EffectApplied);
        Assert.Null((await db.CrmTasks.AsNoTracking().SingleAsync()).AutomationJobId);
        Assert.Equal("pending", (await db.AutomationJobs.AsNoTracking().SingleAsync()).StatusCode);
        await db.GetService<IMigrator>().MigrateAsync(AutomationJobMigrationTests.Current);
        Assert.Equal(before, await Snapshot(db));
        await db.Database.MigrateAsync(); await AssertCurrent(db); Assert.Equal(before, await Snapshot(db));
    }

    private static async Task AssertCurrent(ProspectionCrmDbContext db)
    {
        Assert.Equal(Current, db.Database.GetMigrations().Last());
        Assert.Empty(await db.Database.GetPendingMigrationsAsync()); Assert.False(db.Database.HasPendingModelChanges());
    }
    private static Task<string[]> Snapshot(ProspectionCrmDbContext db) => db.Database.SqlQuery<string>($"""
        SELECT (to_jsonb(t) - 'AutomationJobId')::text AS "Value" FROM "CrmTasks" t
        UNION ALL
        SELECT (to_jsonb(e) - ARRAY['AutomationJobId','AttemptNumber','ActionTypeCode','ReasonCode',
            'IsAutomaticAttempt','IsDeferred','EffectApplied','OutcomeSequence'])::text FROM "AutomationExecutions" e
        UNION ALL SELECT to_jsonb(j)::text FROM "AutomationJobs" j
        ORDER BY "Value"
        """).ToArrayAsync();
}
