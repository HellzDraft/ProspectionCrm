using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using ProspectionCrm.Api.Entities;
using Xunit;

namespace ProspectionCrm.Api.Tests;

public sealed class SourceCollectionSchedulingMigrationTests : PersistentSourceIdentityFixture
{
    private const string Phase73 = "20261007083908_Phase73CollectionRetries";
    [Fact]
    public async Task UpgradeAndDownUpPreserveAllExistingDataWithoutSchedulingOldSearches()
    {
        var setup = await PrepareAsync(Phase73); await using var db = Db();
        var now = DateTimeOffset.UtcNow;
        db.Add(new SourceCollectionJob { WorkspaceId = setup.Workspace, SavedSearchId = setup.Search,
            PipelineId = setup.Pipeline, PipelineStageId = setup.Stage, TriggerTypeCode = "manual", StatusCode = "succeeded",
            EnqueuedAt = now, AvailableAt = now, StartedAt = now, FinishedAt = now, AttemptCount = 1, SourceExecutionId = setup.Execution });
        await db.SaveChangesAsync(); var job = await db.SourceCollectionJobs.SingleAsync();
        db.Add(new SourceCollectionJobAttempt { JobId = job.Id, WorkspaceId = setup.Workspace, AttemptNumber = 1,
            StartedAt = now, FinishedAt = now, StatusCode = "succeeded", SourceExecutionId = setup.Execution });
        await db.SaveChangesAsync(); var before = await Snapshot();
        await db.Database.MigrateAsync(); Assert.Equal(before, await Snapshot());
        Assert.Empty(await db.SourceCollectionSchedules.ToListAsync());
        db.Add(new SourceCollectionSchedule { WorkspaceId = setup.Workspace, PipelineId = setup.Pipeline, SavedSearchId = setup.Search,
            PipelineStageId = setup.Stage, Enabled = true, DailyUtcMinute = 600, NextCollectionAt = now.AddDays(1) });
        await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        await db.GetService<IMigrator>().MigrateAsync(Phase73); Assert.Equal(before, await Snapshot());
        await db.Database.MigrateAsync(); Assert.Equal(before, await Snapshot());
        Assert.Empty(await db.SourceCollectionSchedules.ToListAsync()); // Downgrade deliberately discards configuration only.
        Assert.Empty(await db.Database.GetPendingMigrationsAsync()); Assert.False(db.Database.HasPendingModelChanges());
    }

    private async Task<string[]> Snapshot()
    {
        await using var connection = new NpgsqlConnection(Postgres.GetConnectionString()); await connection.OpenAsync();
        var tables = new List<string>();
        await using (var command = new NpgsqlCommand("""
            SELECT tablename FROM pg_tables WHERE schemaname = 'public'
                AND tablename NOT IN ('__EFMigrationsHistory', 'AutomationRuntimeSettings', 'SourceCollectionSchedules') ORDER BY tablename
            """, connection))
        await using (var reader = await command.ExecuteReaderAsync()) while (await reader.ReadAsync()) tables.Add(reader.GetString(0));
        var snapshots = new List<string>();
        foreach (var table in tables)
        {
            var quoted = new NpgsqlCommandBuilder().QuoteIdentifier(table);
            await using var command = new NpgsqlCommand($"SELECT COALESCE(jsonb_agg(to_jsonb(t) ORDER BY to_jsonb(t)::text), '[]'::jsonb)::text FROM {quoted} t", connection);
            snapshots.Add(table + ":" + (string)(await command.ExecuteScalarAsync())!);
        }
        return snapshots.ToArray();
    }

    [Fact]
    public async Task VirginSchemaProtectsWorkspacePipelineStageAndScheduleState()
    {
        var setup = await PrepareAsync(); await using var db = Db(); var other = await SeedAsync(db, archived: true);
        foreach (var kind in new[] { "workspace", "pipeline", "stage", "minute", "enabled-null", "disabled-date" })
        {
            var schedule = new SourceCollectionSchedule { SavedSearchId = setup.Search, WorkspaceId = setup.Workspace,
                PipelineId = setup.Pipeline, PipelineStageId = setup.Stage, Enabled = true, DailyUtcMinute = 600, NextCollectionAt = DateTimeOffset.UtcNow };
            switch (kind)
            {
                case "workspace": schedule.WorkspaceId = other.Workspace; break;
                case "pipeline": schedule.PipelineId = other.Pipeline; schedule.PipelineStageId = other.Stage; break;
                case "stage": schedule.PipelineStageId = other.Stage; break;
                case "minute": schedule.DailyUtcMinute = 1440; break;
                case "enabled-null": schedule.NextCollectionAt = null; break;
                case "disabled-date": schedule.Enabled = false; break;
            }
            db.Add(schedule); var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            Assert.Equal(kind is "workspace" or "pipeline" or "stage" ? "23503" : "23514", Assert.IsType<PostgresException>(error.InnerException).SqlState);
            db.ChangeTracker.Clear();
        }
        db.Add(new SourceCollectionSchedule { SavedSearchId = setup.Search, WorkspaceId = setup.Workspace,
            PipelineId = setup.Pipeline, PipelineStageId = setup.Stage, Enabled = false, DailyUtcMinute = 600 });
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<PostgresException>(() => db.SavedSearches.Where(x => x.Id == setup.Search).ExecuteDeleteAsync());
        await Assert.ThrowsAsync<PostgresException>(() => db.PipelineStages.Where(x => x.Id == setup.Stage).ExecuteDeleteAsync());
        Assert.Empty(await db.Database.GetPendingMigrationsAsync()); Assert.False(db.Database.HasPendingModelChanges());
    }
}
