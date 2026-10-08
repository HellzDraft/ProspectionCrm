using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using ProspectionCrm.Api.Entities;
using Xunit;

namespace ProspectionCrm.Api.Tests;

public sealed class SourceCollectionJobMigrationTests : PersistentSourceIdentityFixture
{
    private const string BeforeJobs = "20261006101923_Phase622PersistentSourceIdentities";

    [Fact]
    public async Task UpgradePreservesEveryExistingTableAndDownUpIsCoherent()
    {
        await PrepareAsync(BeforeJobs);
        var before = await Snapshot();
        await using var db = Db(); await db.Database.MigrateAsync();
        Assert.Empty(await db.SourceCollectionJobs.ToListAsync());
        Assert.Equal(before, await Snapshot());
        var migrations = db.Database.GetMigrations().ToArray();
        Assert.Equal(BeforeJobs, migrations[^7]); Assert.EndsWith("_Phase71PersistentCollectionJobs", migrations[^6]);
        Assert.EndsWith("_Phase72CollectionWorkerLeases", migrations[^5]);
        Assert.EndsWith("_Phase73CollectionRetries", migrations[^4]);
        Assert.EndsWith("_Phase74CollectionScheduling", migrations[^3]);
        Assert.EndsWith("_Phase81AutomationRuntimeSettings", migrations[^2]);
        Assert.EndsWith("_Phase82AutomationJobs", migrations[^1]);
        Assert.Equal(migrations, await db.Database.GetAppliedMigrationsAsync());
        Assert.Empty(await db.Database.GetPendingMigrationsAsync()); Assert.False(db.Database.HasPendingModelChanges());
        await AssertSchema();
        await db.GetService<IMigrator>().MigrateAsync(BeforeJobs);
        Assert.Equal(before, await Snapshot()); Assert.Equal(BeforeJobs, (await db.Database.GetAppliedMigrationsAsync()).Last());
        await db.Database.MigrateAsync(); Assert.Equal(before, await Snapshot());
        Assert.Empty(await db.SourceCollectionJobs.ToListAsync()); await AssertSchema();
        Assert.Empty(await db.Database.GetPendingMigrationsAsync()); Assert.False(db.Database.HasPendingModelChanges());
    }

    [Fact]
    public async Task FreshPostgresAppliesEntireChainWithoutPendingModelChanges()
    {
        await using var db = Db(); Assert.Empty(await db.Database.GetAppliedMigrationsAsync());
        await db.Database.MigrateAsync();
        Assert.Equal(db.Database.GetMigrations(), await db.Database.GetAppliedMigrationsAsync());
        Assert.Empty(await db.Database.GetPendingMigrationsAsync()); Assert.False(db.Database.HasPendingModelChanges());
        Assert.Empty(await db.SourceCollectionJobs.ToListAsync()); await AssertSchema();
    }

    private async Task<string[]> Snapshot()
    {
        await using var connection = new NpgsqlConnection(Postgres.GetConnectionString()); await connection.OpenAsync();
        var tables = new List<string>();
        await using (var command = new NpgsqlCommand("""
            SELECT tablename FROM pg_tables WHERE schemaname = 'public'
            AND tablename NOT IN ('__EFMigrationsHistory', 'AutomationJobs', 'AutomationRuntimeSettings', 'SourceCollectionJobs', 'SourceCollectionJobAttempts', 'SourceCollectionSchedules') ORDER BY tablename
            """, connection))
        await using (var reader = await command.ExecuteReaderAsync())
            while (await reader.ReadAsync()) tables.Add(reader.GetString(0));
        var snapshots = new List<string>();
        foreach (var table in tables)
        {
            var quoted = new NpgsqlCommandBuilder().QuoteIdentifier(table);
            await using var command = new NpgsqlCommand($"SELECT COALESCE(jsonb_agg(to_jsonb(t) ORDER BY to_jsonb(t)::text), '[]'::jsonb)::text FROM {quoted} t", connection);
            snapshots.Add(table + ":" + (string)(await command.ExecuteScalarAsync())!);
        }
        return snapshots.ToArray();
    }

    private async Task AssertSchema()
    {
        await using var connection = new NpgsqlConnection(Postgres.GetConnectionString()); await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT indexdef FROM pg_indexes WHERE tablename = 'SourceCollectionJobs'", connection);
        var indexes = new List<string>();
        await using (var reader = await command.ExecuteReaderAsync()) while (await reader.ReadAsync()) indexes.Add(reader.GetString(0));
        Assert.Contains(indexes, x => x.Contains("(\"StatusCode\", \"AvailableAt\", \"EnqueuedAt\", \"Id\")"));
        Assert.Contains(indexes, x => x.Contains("(\"WorkspaceId\", \"EnqueuedAt\", \"Id\")"));
        Assert.Contains(indexes, x => x.Contains("(\"WorkspaceId\", \"SavedSearchId\", \"StatusCode\", \"EnqueuedAt\")"));
        Assert.Contains(indexes, x => x.Contains("UNIQUE") && x.Contains("(\"SourceExecutionId\")") && x.Contains("IS NOT NULL"));
        Assert.Contains(indexes, x => x.Contains("UX_SourceCollectionJobs_Workspace_Search_Stage_Active") && x.Contains("UNIQUE")
            && x.Contains("(\"WorkspaceId\", \"SavedSearchId\", \"PipelineStageId\")") && x.Contains("queued") && x.Contains("running") && x.Contains("WHERE"));
        await using var constraints = new NpgsqlCommand("""
            SELECT contype::text, pg_get_constraintdef(oid) FROM pg_constraint WHERE conrelid = '"SourceCollectionJobs"'::regclass
            """, connection);
        var checks = new List<string>(); var foreignKeys = new List<string>();
        await using (var reader = await constraints.ExecuteReaderAsync()) while (await reader.ReadAsync())
        { if (reader.GetString(0) == "c") checks.Add(reader.GetString(1)); if (reader.GetString(0) == "f") foreignKeys.Add(reader.GetString(1)); }
        Assert.Equal(8, checks.Count); Assert.Equal(5, foreignKeys.Count);
        Assert.All(foreignKeys, x => Assert.Contains("ON DELETE RESTRICT", x));
        Assert.Contains(foreignKeys, x => x.Contains("REFERENCES \"SavedSearches\"(\"WorkspaceId\", \"Id\", \"PipelineId\")"));
        Assert.Contains(foreignKeys, x => x.Contains("REFERENCES \"PipelineStages\"(\"PipelineId\", \"Id\")"));
        Assert.Contains(foreignKeys, x => x.Contains("REFERENCES \"SourceExecutions\"(\"WorkspaceId\", \"Id\")"));
    }

    private static SourceCollectionJob Job(Setup setup)
    {
        var now = DateTimeOffset.UtcNow;
        return new() { WorkspaceId = setup.Workspace, SavedSearchId = setup.Search, PipelineId = setup.Pipeline,
            PipelineStageId = setup.Stage, TriggerTypeCode = "manual", StatusCode = "queued", EnqueuedAt = now, AvailableAt = now };
    }

    [Theory]
    [InlineData("workspace", "23503")]
    [InlineData("search-pipeline", "23503")]
    [InlineData("stage-pipeline", "23503")]
    [InlineData("execution-workspace", "23503")]
    [InlineData("status", "23514")]
    [InlineData("trigger", "23514")]
    [InlineData("attempt", "23514")]
    [InlineData("available", "23514")]
    [InlineData("started", "23514")]
    [InlineData("finished", "23514")]
    [InlineData("queued-start", "23514")]
    [InlineData("queued-finish", "23514")]
    [InlineData("queued-execution", "23514")]
    [InlineData("queued-error", "23514")]
    [InlineData("running-start", "23514")]
    [InlineData("running-finish", "23514")]
    [InlineData("running-error", "23514")]
    [InlineData("succeeded-start", "23514")]
    [InlineData("succeeded-finish", "23514")]
    [InlineData("succeeded-execution", "23514")]
    [InlineData("succeeded-error", "23514")]
    [InlineData("failed-start", "23514")]
    [InlineData("failed-finish", "23514")]
    [InlineData("failed-error", "23514")]
    [InlineData("cancelled-finish", "23514")]
    public async Task PostgreSqlRejectsInvalidReferencesTimestampsAndStateShapes(string kind, string state)
    {
        var setup = await PrepareAsync(); await using var db = Db(); var other = await SeedAsync(db, archived: true);
        var otherPipeline = new Pipeline { WorkspaceId = setup.Workspace, Name = "Same workspace", TypeCode = "custom" };
        var otherStage = new PipelineStage { Pipeline = otherPipeline, Name = "Other stage", CategoryCode = "active" };
        db.PipelineStages.Add(otherStage); await db.SaveChangesAsync();
        var job = Job(setup);
        switch (kind)
        {
            case "workspace": job.WorkspaceId = other.Workspace; break;
            case "search-pipeline": job.PipelineId = otherPipeline.Id; job.PipelineStageId = otherStage.Id; break;
            case "stage-pipeline": job.PipelineStageId = otherStage.Id; break;
            case "execution-workspace": job.StatusCode = "running"; job.StartedAt = job.AvailableAt; job.SourceExecutionId = other.Execution; break;
            case "status": job.StatusCode = "unknown"; break;
            case "trigger": job.TriggerTypeCode = "unknown"; break;
            case "attempt": job.AttemptCount = -1; break;
            case "available": job.AvailableAt = job.EnqueuedAt.AddSeconds(-1); break;
            case "started": job.StatusCode = "running"; job.StartedAt = job.AvailableAt.AddSeconds(-1); break;
            case "finished": job.StatusCode = "cancelled"; job.StartedAt = job.AvailableAt; job.FinishedAt = job.StartedAt.Value.AddSeconds(-1); break;
            default:
                var parts = kind.Split('-'); job.StatusCode = parts[0];
                if (job.StatusCode != "queued") job.StartedAt = job.AvailableAt;
                if (job.StatusCode is "succeeded" or "failed" or "cancelled") job.FinishedAt = job.AvailableAt;
                if (job.StatusCode == "succeeded") job.SourceExecutionId = setup.Execution;
                if (job.StatusCode == "failed") job.ErrorCode = "Failure";
                switch (parts[1])
                {
                    case "start": job.StartedAt = job.StatusCode == "queued" ? job.AvailableAt : null; break;
                    case "finish": job.FinishedAt = job.StatusCode is "queued" or "running" ? job.AvailableAt : null; break;
                    case "execution": job.SourceExecutionId = job.StatusCode == "queued" ? setup.Execution : null; break;
                    case "error": job.ErrorCode = job.StatusCode == "failed" ? null : "Failure"; break;
                }
                break;
        }
        if (job.StatusCode == "running") { job.LeaseToken = Guid.NewGuid(); job.LeaseExpiresAt = job.AvailableAt.AddMinutes(5); }
        db.SourceCollectionJobs.Add(job);
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Equal(state, Assert.IsType<PostgresException>(error.InnerException).SqlState);
    }

    [Fact]
    public async Task UniqueIndexesProtectActiveCommandsAndExecutionWhileAllowingTerminalHistory()
    {
        var setup = await PrepareAsync(); await using var db = Db();
        var active = Job(setup); db.Add(active); await db.SaveChangesAsync();
        var duplicate = Job(setup); db.Add(duplicate);
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Equal("UX_SourceCollectionJobs_Workspace_Search_Stage_Active", Assert.IsType<PostgresException>(error.InnerException).ConstraintName);
        db.ChangeTracker.Clear();
        await db.SourceCollectionJobs.ExecuteUpdateAsync(s => s.SetProperty(x => x.StatusCode, "cancelled").SetProperty(x => x.FinishedAt, DateTimeOffset.UtcNow));
        foreach (var status in new[] { "cancelled", "failed", "succeeded" })
        {
            var job = Job(setup); job.StatusCode = status; job.StartedAt = job.AvailableAt; job.FinishedAt = job.AvailableAt;
            job.ErrorCode = status == "failed" ? "Failure" : null; job.SourceExecutionId = status == "succeeded" ? setup.Execution : null;
            db.Add(job);
        }
        await db.SaveChangesAsync(); Assert.Equal(4, await db.SourceCollectionJobs.CountAsync());
        var reused = Job(setup); reused.StatusCode = "cancelled"; reused.FinishedAt = reused.AvailableAt; reused.SourceExecutionId = setup.Execution; db.Add(reused);
        error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        var pg = Assert.IsType<PostgresException>(error.InnerException); Assert.Equal("23505", pg.SqlState);
        Assert.Equal("IX_SourceCollectionJobs_SourceExecutionId", pg.ConstraintName);
        db.ChangeTracker.Clear(); var running = Job(setup); running.StatusCode = "running"; running.StartedAt = running.AvailableAt;
        running.LeaseToken = Guid.NewGuid(); running.LeaseExpiresAt = running.AvailableAt.AddMinutes(5);
        db.Add(running); await db.SaveChangesAsync(); db.Add(Job(setup));
        error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync()); Assert.Equal("23505", Assert.IsType<PostgresException>(error.InnerException).SqlState);
    }
}
