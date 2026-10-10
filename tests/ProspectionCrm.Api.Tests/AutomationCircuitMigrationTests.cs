using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace ProspectionCrm.Api.Tests;

public sealed class AutomationCircuitMigrationTests(AutomationJobDatabase database)
    : AutomationSupervisionFixture(database), IClassFixture<AutomationJobDatabase>
{
    public const string Previous = "20261009115938_Phase85AutomationActionRequests";

    [Fact]
    public async Task FreshChainAndPopulatedUpgradeDownUpPreserveEveryPriorBusinessRow()
    {
        await using var db = Db();
        Assert.EndsWith("_Phase86AutomationCircuitResets",(await db.Database.GetAppliedMigrationsAsync()).Last());
        Assert.False(db.Database.HasPendingModelChanges()); Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        var setup = await Approved(); await Run(setup);
        await Settings(setup.Workspace,s => s.MaxConsecutiveFailures = 1);
        await Outcome(setup,1,false);
        var waiting = await Prepare("assist",workspace:setup.Workspace); await Run(waiting);
        var before = await BusinessSnapshot(setup.Workspace);
        await db.GetService<IMigrator>().MigrateAsync(Previous);
        Assert.Equal(before,await BusinessSnapshot(setup.Workspace));
        Assert.Equal(0,await db.Database.SqlQuery<int>($"""
            SELECT count(*)::int AS "Value" FROM pg_tables WHERE tablename = 'AutomationCircuitResets'
            """).SingleAsync());
        await db.Database.MigrateAsync();
        Assert.Empty(await db.AutomationCircuitResets.ToListAsync()); Assert.Equal(before,await BusinessSnapshot(setup.Workspace));
        Assert.Equal("open",(await Circuit(setup.Workspace)).StatusCode);
        await Reset(setup.Workspace); Assert.Equal("closed",(await Circuit(setup.Workspace)).StatusCode);
        Assert.Equal(before,await BusinessSnapshot(setup.Workspace));
        await db.GetService<IMigrator>().MigrateAsync(Previous);
        Assert.Equal(before,await BusinessSnapshot(setup.Workspace));
        // The 8.5 circuit again sees the pre-reset outcome; downgrade never fabricates a success.
        Assert.Equal(1,await db.AutomationExecutions.CountAsync(x => x.WorkspaceId == setup.Workspace
            && x.IsAutomaticAttempt && x.OutcomeSequence != null && !x.EffectApplied));
        await db.Database.MigrateAsync();
        Assert.Equal("open",(await Circuit(setup.Workspace)).StatusCode);
        Assert.Empty(await db.AutomationCircuitResets.ToListAsync());
        Assert.Equal(before,await BusinessSnapshot(setup.Workspace));
        Assert.False(db.Database.HasPendingModelChanges()); Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.Equal(db.Database.GetMigrations(),await db.Database.GetAppliedMigrationsAsync());
    }
}
