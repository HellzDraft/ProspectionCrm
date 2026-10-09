using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace ProspectionCrm.Api.Tests;

public sealed class AutomationActionRequestMigrationTests(AutomationJobDatabase database)
    : AutomationActionRequestFixture(database), IClassFixture<AutomationJobDatabase>
{
    public const string Previous = "20261008124357_Phase84AutomationExecutionRuntime";
    public const string Current = "20261009115938_Phase85AutomationActionRequests";

    [Fact]
    public async Task FreshUpgradeAndPopulatedDownUpPreserveJobsTasksAndHistoriesWithoutBackfill()
    {
        await using var db = Db();
        Assert.False(db.Database.HasPendingModelChanges()); Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.Equal(Current, (await db.Database.GetAppliedMigrationsAsync()).Last());
        await db.GetService<IMigrator>().MigrateAsync(Previous);
        var legacy = await Prepare("manual");
        var historyId = Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "AutomationJobs" SET "StatusCode" = 'completed', "CompletedAt" = now(), "AttemptCount" = 1 WHERE "Id" = {legacy.Job.Id};
            INSERT INTO "AutomationExecutions" ("Id","WorkspaceId","AutomationRuleId","AutomationJobId","AttemptNumber",
                "StatusCode","TriggeredAt","StartedAt","FinishedAt","ReasonCode")
            VALUES ({historyId},{legacy.Workspace},{legacy.Rule.Id},{legacy.Job.Id},1,'skipped',now(),now(),now(),'eligible-manual');
            """);
        var before = await LegacySnapshot(historyId);
        await db.Database.MigrateAsync(); Assert.Equal(before, await LegacySnapshot(historyId));
        Assert.Empty(await db.AutomationActionRequests.ToListAsync());
        var approved = await Approved(); await Run(approved); var waiting = await Prepare("assist"); await Run(waiting);
        var task = Assert.Single(await Tasks(approved.Job.Id));
        var counts = new[] { await db.AutomationJobs.CountAsync(), await db.AutomationExecutions.CountAsync(), await db.CrmTasks.CountAsync() };
        await db.GetService<IMigrator>().MigrateAsync(Previous);
        Assert.Equal(before, await LegacySnapshot(historyId));
        Assert.Equal("pending", (await Job(waiting.Job.Id)).StatusCode);
        Assert.Equal(task.Id, Assert.Single(await Tasks(approved.Job.Id)).Id);
        Assert.Equal(0, await db.Database.SqlQuery<int>($"""
            SELECT count(*)::int AS "Value" FROM pg_tables WHERE tablename = 'AutomationActionRequests'
            """).SingleAsync());
        await db.Database.MigrateAsync();
        Assert.Equal(counts, new[] { await db.AutomationJobs.CountAsync(), await db.AutomationExecutions.CountAsync(), await db.CrmTasks.CountAsync() });
        Assert.Empty(await db.AutomationActionRequests.ToListAsync());
        var downgraded = Assert.Single(await Executions(approved.Job.Id));
        Assert.Equal("succeeded", downgraded.StatusCode); Assert.False(downgraded.IsAutomaticAttempt);
        Assert.False(downgraded.IsHumanApprovedAttempt); Assert.False(downgraded.EffectApplied); Assert.Null(downgraded.OutcomeSequence);
        Assert.Equal(before, await LegacySnapshot(historyId)); Assert.False(db.Database.HasPendingModelChanges());
    }

    private async Task<string> LegacySnapshot(Guid id)
    {
        await using var db = Db();
        return await db.Database.SqlQuery<string>($"""
            SELECT (to_jsonb(e) - ARRAY['AutomationActionRequestId','IsHumanApprovedAttempt'])::text AS "Value"
            FROM "AutomationExecutions" e WHERE "Id" = {id}
            """).SingleAsync();
    }
}
