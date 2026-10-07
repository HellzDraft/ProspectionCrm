using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using ProspectionCrm.Api.Entities;
using Xunit;

namespace ProspectionCrm.Api.Tests;

public sealed class SourceCollectionRetryMigrationTests : PersistentSourceIdentityFixture
{
    private const string Phase72 = "20261007074738_Phase72CollectionWorkerLeases";
    [Fact]
    public async Task UpgradePreservesEveryJobAndExecutionAndBackfillsKnownAttemptsOnly()
    {
        var setup = await PrepareAsync(Phase72); await using var db = Db(); var sort = 1;
        foreach (var status in new[] { "queued", "running", "succeeded", "failed", "cancelled" })
        {
            var now = DateTimeOffset.UtcNow.AddHours(-1);
            var stage = new PipelineStage { PipelineId = setup.Pipeline, Name = status, CategoryCode = "active", SortOrder = sort++ };
            var started = status is "queued" or "cancelled" ? (DateTimeOffset?)null : now;
            db.Add(new SourceCollectionJob { WorkspaceId = setup.Workspace, SavedSearchId = setup.Search, PipelineId = setup.Pipeline,
                PipelineStage = stage, StatusCode = status, TriggerTypeCode = "manual", EnqueuedAt = now, AvailableAt = now,
                StartedAt = started, FinishedAt = status is "queued" or "running" ? null : now.AddMinutes(1),
                AttemptCount = started is null ? 0 : 1, SourceExecutionId = status == "succeeded" ? setup.Execution : null,
                ErrorCode = status == "failed" ? "UpstreamTimeout" : null,
                LeaseToken = status == "running" ? Guid.NewGuid() : null, LeaseExpiresAt = status == "running" ? now.AddMinutes(5) : null });
        }
        await db.SaveChangesAsync(); var before = await Snapshot();
        await db.Database.MigrateAsync(); Assert.Equal(before, await Snapshot());
        Assert.Equal(3, await db.SourceCollectionJobAttempts.CountAsync());
        var linked = await db.SourceCollectionJobAttempts.AsNoTracking().SingleAsync(x => x.SourceExecutionId != null);
        Assert.Equal(setup.Execution, linked.SourceExecutionId); Assert.Equal(1, linked.AttemptNumber);
        Assert.Equal("succeeded", linked.StatusCode); Assert.Null(linked.ErrorCode);
        Assert.Empty(await db.Database.GetPendingMigrationsAsync()); Assert.False(db.Database.HasPendingModelChanges());
        await db.GetService<IMigrator>().MigrateAsync(Phase72); Assert.Equal(before, await Snapshot());
        await db.Database.MigrateAsync(); Assert.Equal(before, await Snapshot()); Assert.Equal(3, await db.SourceCollectionJobAttempts.CountAsync());
        Assert.Empty(await db.Database.GetPendingMigrationsAsync()); Assert.False(db.Database.HasPendingModelChanges());
    }
    private async Task<string> Snapshot()
    {
        await using var connection = new NpgsqlConnection(Postgres.GetConnectionString()); await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT jsonb_build_object('jobs', (SELECT jsonb_agg(to_jsonb(j) ORDER BY "Id") FROM "SourceCollectionJobs" j),
                'executions', (SELECT jsonb_agg(to_jsonb(e) ORDER BY "Id") FROM "SourceExecutions" e))::text
            """, connection);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    [Fact]
    public async Task VirginDatabaseAndAttemptsConstraintsProtectWorkspaceHistory()
    {
        var setup = await PrepareAsync(); await using var db = Db();
        var other = await SeedAsync(db, archived: true);
        var now = DateTimeOffset.UtcNow;
        var job = new SourceCollectionJob { WorkspaceId = setup.Workspace, SavedSearchId = setup.Search, PipelineId = setup.Pipeline,
            PipelineStageId = setup.Stage, StatusCode = "queued", TriggerTypeCode = "manual", EnqueuedAt = now, AvailableAt = now };
        db.Add(job); await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        foreach (var kind in new[] { "workspace", "execution-workspace", "negative-number", "http", "dates", "status" })
        {
            var attempt = new SourceCollectionJobAttempt { JobId = job.Id, WorkspaceId = setup.Workspace, AttemptNumber = 1,
                StartedAt = now, StatusCode = "running" };
            switch (kind)
            {
                case "workspace": attempt.WorkspaceId = other.Workspace; break;
                case "execution-workspace": attempt.SourceExecutionId = other.Execution; break;
                case "negative-number": attempt.AttemptNumber = -1; break;
                case "http": attempt.UpstreamStatusCode = 999; break;
                case "dates": attempt.StatusCode = "failed"; attempt.FinishedAt = now.AddSeconds(-1); break;
                case "status": attempt.StatusCode = "unknown"; break;
            }
            db.Add(attempt); var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            Assert.Equal(kind.Contains("workspace") ? "23503" : "23514", Assert.IsType<PostgresException>(error.InnerException).SqlState);
            db.ChangeTracker.Clear();
        }
        db.Add(new SourceCollectionJobAttempt { JobId = job.Id, WorkspaceId = setup.Workspace, AttemptNumber = 1,
            StartedAt = now, StatusCode = "succeeded", FinishedAt = now, SourceExecutionId = setup.Execution }); await db.SaveChangesAsync();
        db.Add(new SourceCollectionJobAttempt { JobId = job.Id, WorkspaceId = setup.Workspace, AttemptNumber = 2,
            StartedAt = now, StatusCode = "succeeded", FinishedAt = now, SourceExecutionId = setup.Execution });
        var duplicate = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Equal("23505", Assert.IsType<PostgresException>(duplicate.InnerException).SqlState);
        Assert.Empty(await db.Database.GetPendingMigrationsAsync()); Assert.False(db.Database.HasPendingModelChanges());
    }
}
