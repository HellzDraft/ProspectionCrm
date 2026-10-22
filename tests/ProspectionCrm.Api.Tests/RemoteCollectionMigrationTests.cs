using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using ProspectionCrm.Api.Data;
using ProspectionCrm.Api.Dtos.Integrations;
using ProspectionCrm.Api.Entities;
using ProspectionCrm.Api.Services.Integrations;
using Xunit;

namespace ProspectionCrm.Api.Tests;

public sealed class RemoteCollectionMigrationTests : PersistentSourceIdentityFixture
{
    public const string Current = "20261015104738_Phase941RemoteCollectionLifecycle";

    [Fact]
    public async Task UpgradeFrom932AndDownUpPreserveHistoricalJobsAttemptsExecutionsAndProbes()
    {
        var setup = await PrepareAsync(IntegrationSecurityMigrationTests.Current); await using var db = Db();
        var sort = 1;
        foreach (var status in new[] { "queued", "running", "succeeded", "failed", "cancelled" })
        {
            var now = DateTimeOffset.UtcNow.AddHours(-1);
            var stage = new PipelineStage { PipelineId = setup.Pipeline, Name = status, CategoryCode = "active", SortOrder = sort++ };
            var activeAttempt = status is "running" or "succeeded" or "failed";
            var job = new SourceCollectionJob { WorkspaceId = setup.Workspace, SavedSearchId = setup.Search, PipelineId = setup.Pipeline,
                PipelineStage = stage, TriggerTypeCode = "manual", StatusCode = status, EnqueuedAt = now, AvailableAt = now,
                StartedAt = activeAttempt ? now : null, FinishedAt = status is "queued" or "running" ? null : now.AddMinutes(1),
                AttemptCount = activeAttempt ? 1 : 0, ErrorCode = status == "failed" ? "UpstreamTimeout" : null,
                SourceExecutionId = status == "succeeded" ? setup.Execution : null,
                LeaseToken = status == "running" ? Guid.NewGuid() : null, LeaseExpiresAt = status == "running" ? now.AddMinutes(5) : null };
            db.Add(job);
            if (activeAttempt) db.Add(new SourceCollectionJobAttempt { Job = job, WorkspaceId = setup.Workspace, AttemptNumber = 1,
                StartedAt = now, StatusCode = status, FinishedAt = status == "running" ? null : now.AddMinutes(1),
                ErrorCode = job.ErrorCode, SourceExecutionId = job.SourceExecutionId });
        }
        await db.SaveChangesAsync();
        var store = new IntegrationStore(db, TimeProvider.System);
        await store.CreateProbeAsync(setup.Workspace); // Legacy NULL connection remains valid and unbound.
        var op = await store.CreateProbeAsync(setup.Workspace, Guid.NewGuid()); await store.BeginAttemptAsync(setup.Workspace, op.Id, 0);
        var result = IntegrationContracts.Serialize(new IntegrationResultEnvelope(1, Guid.NewGuid(), op.Id, op.CorrelationId, op.CommandMessageId,
            op.Type, new(op.ProbeId, "succeeded")));
        await store.ReceiveAsync(new(setup.Workspace, op.ConnectionId!.Value), result);
        var before = await Snapshot(db);
        await db.Database.MigrateAsync(); Assert.Equal(before, await Snapshot(db)); Assert.Empty(await db.RemoteCollectionIntents.ToListAsync());
        Assert.False(db.Database.HasPendingModelChanges()); Assert.Equal(CalendarEventDeliveryMigrationTests.Current, (await db.Database.GetAppliedMigrationsAsync()).Last());
        await db.GetService<IMigrator>().MigrateAsync(IntegrationSecurityMigrationTests.Current); Assert.Equal(before, await Snapshot(db));
        await db.Database.MigrateAsync(); Assert.Equal(before, await Snapshot(db));
        Assert.Empty(await db.RemoteCollectionIntents.ToListAsync()); Assert.False(db.Database.HasPendingModelChanges());
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
    }

    [Fact]
    public async Task FreshDatabaseEnforcesRemoteStatesIdentityConnectionAndOneToOneBinding()
    {
        var setup = await PrepareAsync(); await using var db = Db();
        var other = await SeedAsync(db); var now = DateTimeOffset.UtcNow;
        var job = new SourceCollectionJob { WorkspaceId = setup.Workspace, SavedSearchId = setup.Search, PipelineId = setup.Pipeline,
            PipelineStageId = setup.Stage, TriggerTypeCode = "manual", StatusCode = "awaiting-result", EnqueuedAt = now, AvailableAt = now,
            StartedAt = now, AttemptCount = 1 };
        var second = new SourceCollectionJob { WorkspaceId = other.Workspace, SavedSearchId = other.Search, PipelineId = other.Pipeline,
            PipelineStageId = other.Stage, TriggerTypeCode = "manual", StatusCode = "queued", EnqueuedAt = now, AvailableAt = now };
        var op = new IntegrationOperation { Id = Guid.NewGuid(), WorkspaceId = setup.Workspace, ConnectionId = Guid.NewGuid(),
            CommandMessageId = Guid.NewGuid(), CorrelationId = Guid.NewGuid(), ProbeId = Guid.Empty, Version = 1,
            Type = CollectionIntegrationContracts.Type, StatusCode = "awaiting-result", CreatedAt = now };
        db.AddRange(job, second, op); await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        RemoteCollectionIntent Link() => new() { JobId = job.Id, WorkspaceId = setup.Workspace, OperationId = op.Id,
            ConnectionId = op.ConnectionId!.Value, OperationType = op.Type, SourceConfigurationId = setup.Configuration,
            Fingerprint = new string('a', 64), ContextSnapshotJson = "{}", CommandJson = "{}", CreatedAt = now };
        foreach (var invalid in new[] { "connection", "workspace", "probe" })
        {
            var link = Link();
            if (invalid == "connection") link.ConnectionId = Guid.NewGuid();
            if (invalid == "workspace") { link.JobId = second.Id; link.WorkspaceId = other.Workspace; link.SourceConfigurationId = other.Configuration; }
            if (invalid == "probe")
            {
                var probe = await new IntegrationStore(db, TimeProvider.System).CreateProbeAsync(setup.Workspace, op.ConnectionId);
                link.OperationId = probe.Id;
            }
            db.Add(link); var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            Assert.Equal("23503", Assert.IsType<PostgresException>(error.InnerException).SqlState); db.ChangeTracker.Clear();
        }
        db.Add(Link()); await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        var extraStage = new PipelineStage { PipelineId = setup.Pipeline, Name = "Other stage", CategoryCode = "active", SortOrder = 1 };
        var extraJob = new SourceCollectionJob { WorkspaceId = setup.Workspace, SavedSearchId = setup.Search, PipelineId = setup.Pipeline,
            PipelineStage = extraStage, StatusCode = "queued", TriggerTypeCode = "manual", EnqueuedAt = now, AvailableAt = now };
        db.Add(extraJob); await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        var duplicate = Link(); duplicate.JobId = extraJob.Id; db.Add(duplicate);
        var reusedOperation = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Equal("23505", Assert.IsType<PostgresException>(reusedOperation.InnerException).SqlState); db.ChangeTracker.Clear();
        var extraOp = new IntegrationOperation { Id = Guid.NewGuid(), WorkspaceId = setup.Workspace, ConnectionId = op.ConnectionId,
            CommandMessageId = Guid.NewGuid(), CorrelationId = Guid.NewGuid(), ProbeId = Guid.Empty, Version = 1,
            Type = op.Type, StatusCode = "pending", CreatedAt = now };
        db.Add(extraOp); await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        duplicate = Link(); duplicate.OperationId = extraOp.Id; db.Add(duplicate);
        var reusedJob = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Equal("23505", Assert.IsType<PostgresException>(reusedJob.InnerException).SqlState); db.ChangeTracker.Clear();
        // The referenced connection cannot be changed behind an already authorized job.
        var mismatch = await Assert.ThrowsAsync<PostgresException>(() => db.IntegrationOperations.Where(x => x.Id == op.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.ConnectionId, Guid.NewGuid())));
        Assert.Equal("23503", mismatch.SqlState);
        foreach (var invalid in new[] { "lease", "finished", "error", "started", "attempts", "unknown-null" })
        {
            var sql = invalid switch
            {
                "lease" => "\"LeaseToken\" = gen_random_uuid()",
                "finished" => "\"FinishedAt\" = statement_timestamp()",
                "error" => "\"ErrorCode\" = 'Bad'",
                "started" => "\"StartedAt\" = NULL",
                "unknown-null" => "\"StatusCode\" = 'dispatch-unknown', \"ErrorCode\" = NULL",
                _ => "\"AttemptCount\" = 0"
            };
            // Only the closed, constant SQL fragments above are interpolated; the id remains a parameter.
            var update = "UPDATE \"SourceCollectionJobs\" SET " + sql + " WHERE \"Id\" = {0}";
            var error = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(update, job.Id));
            Assert.Equal("23514", error.SqlState);
        }
        db.Add(new SourceCollectionJobAttempt { JobId = job.Id, WorkspaceId = setup.Workspace, AttemptNumber = 1,
            StartedAt = now, StatusCode = "dispatch-unknown" });
        var unknownWithoutCode = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Equal("23514", Assert.IsType<PostgresException>(unknownWithoutCode.InnerException).SqlState); db.ChangeTracker.Clear();
        // No destructive downgrade once the new durable history exists.
        await Assert.ThrowsAsync<PostgresException>(() => db.GetService<IMigrator>().MigrateAsync(IntegrationSecurityMigrationTests.Current));
        // Phases953/951 are empty here; their Down commits before Phase941 refuses to erase its history.
        Assert.Single(await db.RemoteCollectionIntents.ToListAsync()); Assert.Equal(Current, (await db.Database.GetAppliedMigrationsAsync()).Last());
        Assert.Equal(new[] { EmailSyncMigrationTests.Current, EmailSendPlanMigrationTests.Current, EmailSendDeliveryMigrationTests.Current, CalendarSyncMigrationTests.Current, CalendarEventPlanMigrationTests.Current, CalendarEventDeliveryMigrationTests.Current }, await db.Database.GetPendingMigrationsAsync());
        await db.Database.MigrateAsync();
        Assert.Single(await db.RemoteCollectionIntents.ToListAsync());
        Assert.Equal(CalendarEventDeliveryMigrationTests.Current, (await db.Database.GetAppliedMigrationsAsync()).Last());
        Assert.False(db.Database.HasPendingModelChanges());
    }

    private static Task<string[]> Snapshot(ProspectionCrmDbContext db) => db.Database.SqlQuery<string>($"""
        SELECT to_jsonb(j)::text AS "Value" FROM "SourceCollectionJobs" j
        UNION ALL SELECT to_jsonb(a)::text FROM "SourceCollectionJobAttempts" a
        UNION ALL SELECT to_jsonb(e)::text FROM "SourceExecutions" e
        UNION ALL SELECT to_jsonb(o)::text FROM "IntegrationOperations" o
        UNION ALL SELECT to_jsonb(a)::text FROM "IntegrationAttempts" a
        UNION ALL SELECT (to_jsonb(r) - 'EvidenceNumber')::text FROM "IntegrationReceipts" r
        UNION ALL SELECT to_jsonb(o)::text FROM "Opportunities" o
        ORDER BY "Value"
        """).ToArrayAsync();
}
