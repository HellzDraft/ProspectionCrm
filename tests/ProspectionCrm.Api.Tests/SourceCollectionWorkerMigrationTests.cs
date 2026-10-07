using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Options;
using Npgsql;
using ProspectionCrm.Api.Entities;
using ProspectionCrm.Api.Services.Collection;
using Xunit;

namespace ProspectionCrm.Api.Tests;

public sealed class SourceCollectionWorkerMigrationTests : PersistentSourceIdentityFixture
{
    private const string Phase71 = "20261007064337_Phase71PersistentCollectionJobs";
    [Fact]
    public async Task UpgradeAndDownUpPreserveAllPhase71JobFieldsIncludingLegacyRunningRows()
    {
        var setup = await PrepareAsync(Phase71); await using var db = Db();
        var sortOrder = 1;
        foreach (var status in new[] { "queued", "running", "succeeded", "failed", "cancelled" })
        {
            var stage = new PipelineStage { PipelineId = setup.Pipeline, Name = status, CategoryCode = "active", SortOrder = sortOrder++ };
            db.Add(stage); await db.SaveChangesAsync();
            var now = DateTimeOffset.UtcNow.AddHours(-1);
            DateTimeOffset? started = status == "queued" ? null : now;
            DateTimeOffset? finished = status is "queued" or "running" ? null : now.AddSeconds(10);
            Guid? execution = status == "succeeded" ? setup.Execution : null;
            string? error = status == "failed" ? "LegacyFailure" : null;
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "SourceCollectionJobs" ("Id", "WorkspaceId", "SavedSearchId", "PipelineId", "PipelineStageId",
                    "TriggerTypeCode", "StatusCode", "EnqueuedAt", "AvailableAt", "StartedAt", "FinishedAt", "AttemptCount", "SourceExecutionId", "ErrorCode")
                VALUES ({Guid.NewGuid()}, {setup.Workspace}, {setup.Search}, {setup.Pipeline}, {stage.Id}, 'manual', {status},
                    {now}, {now}, {started}, {finished}, {(status == "queued" ? 0 : 1)}, {execution}, {error})
                """);
        }
        var before = await Snapshot();
        await db.Database.MigrateAsync(); Assert.Equal(before, await Snapshot());
        Assert.Empty(await db.Database.GetPendingMigrationsAsync()); Assert.False(db.Database.HasPendingModelChanges());
        var running = await db.SourceCollectionJobs.AsNoTracking().SingleAsync(x => x.StatusCode == "running");
        Assert.NotNull(running.LeaseToken); Assert.NotEqual(Guid.Empty, running.LeaseToken);
        Assert.True(running.LeaseExpiresAt > running.StartedAt); Assert.True(running.LeaseExpiresAt < DateTimeOffset.UtcNow);
        Assert.All(await db.SourceCollectionJobs.Where(x => x.StatusCode != "running").ToListAsync(), x =>
        { Assert.Null(x.LeaseToken); Assert.Null(x.LeaseExpiresAt); });
        await db.GetService<IMigrator>().MigrateAsync(Phase71); Assert.Equal(before, await Snapshot());
        await db.Database.MigrateAsync(); Assert.Equal(before, await Snapshot());
        Assert.Empty(await db.Database.GetPendingMigrationsAsync()); Assert.False(db.Database.HasPendingModelChanges());
        var queue = new SourceCollectionJobQueue(db, Options.Create(new SourceCollectionWorkerOptions()));
        var claimed = await queue.ClaimAsync(default); Assert.NotNull(claimed); Assert.NotEqual(running.Id, claimed.Id);
        Assert.Null(await queue.ClaimAsync(default)); // legacy running row is never reclaimed
    }

    private async Task<string> Snapshot()
    {
        await using var connection = new NpgsqlConnection(Postgres.GetConnectionString()); await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT jsonb_agg(to_jsonb(j) - 'LeaseToken' - 'LeaseExpiresAt' ORDER BY "Id")::text FROM "SourceCollectionJobs" j
            """, connection);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    [Theory]
    [InlineData("running-token")]
    [InlineData("running-expiry")]
    [InlineData("running-empty-token")]
    [InlineData("running-invalid-expiry")]
    [InlineData("queued-lease")]
    [InlineData("terminal-lease")]
    public async Task PostgreSqlEnforcesLeaseStateShapes(string kind)
    {
        var setup = await PrepareAsync(); await using var db = Db(); var now = DateTimeOffset.UtcNow;
        var job = new SourceCollectionJob { WorkspaceId = setup.Workspace, SavedSearchId = setup.Search,
            PipelineId = setup.Pipeline, PipelineStageId = setup.Stage, TriggerTypeCode = "manual", StatusCode = "running",
            EnqueuedAt = now, AvailableAt = now, StartedAt = now, AttemptCount = 1, LeaseToken = Guid.NewGuid(), LeaseExpiresAt = now.AddMinutes(5) };
        switch (kind)
        {
            case "running-token": job.LeaseToken = null; break;
            case "running-expiry": job.LeaseExpiresAt = null; break;
            case "running-empty-token": job.LeaseToken = Guid.Empty; break;
            case "running-invalid-expiry": job.LeaseExpiresAt = now; break;
            case "queued-lease": job.StatusCode = "queued"; job.StartedAt = null; break;
            case "terminal-lease": job.StatusCode = "cancelled"; job.FinishedAt = now; break;
        }
        db.Add(job); var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Equal("CK_SourceCollectionJobs_Lease", Assert.IsType<PostgresException>(error.InnerException).ConstraintName);
    }
}
