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

public sealed class EmailSyncMigrationTests : PersistentSourceIdentityFixture
{
    public const string Current = "20261016093720_Phase951InboundEmailSync";

    [Fact]
    public async Task UpgradeFrom941PreservesLegacyEmailsProbesCollectionAndSafeDownUp()
    {
        var s = await PrepareAsync(RemoteCollectionMigrationTests.Current); await using var db = Db();
        foreach (var direction in new[] { "inbound", "outbound" })
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "EmailMessages" ("Id","WorkspaceId","ProviderCode","ExternalMessageId","DirectionCode","FromAddress","ToAddressesJson","OccurredAt","CreatedAt","BodyText")
                VALUES ({Guid.NewGuid()},{s.Workspace},'legacy',{direction},{direction},'legacy@example.invalid','[]',statement_timestamp(),statement_timestamp(),'Historical body')
                """);
        var store = new IntegrationStore(db, TimeProvider.System);
        var probe = await store.CreateProbeAsync(s.Workspace); Assert.Null(probe.ConnectionId);
        var connected = await store.CreateProbeAsync(s.Workspace, Guid.NewGuid());
        await store.BeginAttemptAsync(s.Workspace, connected.Id, 0);
        await store.ReceiveAsync(new(s.Workspace, connected.ConnectionId!.Value), IntegrationContracts.Serialize(new IntegrationResultEnvelope(1,
            Guid.NewGuid(), connected.Id, connected.CorrelationId, connected.CommandMessageId, connected.Type, new(connected.ProbeId, "succeeded"))));
        var now = DateTimeOffset.UtcNow;
        var collectionOp = new IntegrationOperation { Id = Guid.NewGuid(), WorkspaceId = s.Workspace, ConnectionId = Guid.NewGuid(),
            CommandMessageId = Guid.NewGuid(), CorrelationId = Guid.NewGuid(), Type = CollectionIntegrationContracts.Type,
            Version = 1, StatusCode = "pending", CreatedAt = now };
        db.Add(collectionOp);
        var collectionJob = new SourceCollectionJob { WorkspaceId = s.Workspace, SavedSearchId = s.Search, PipelineId = s.Pipeline,
            PipelineStageId = s.Stage, TriggerTypeCode = "manual", StatusCode = "queued", EnqueuedAt = now, AvailableAt = now };
        db.Add(collectionJob); await db.SaveChangesAsync();
        db.Add(new RemoteCollectionIntent { JobId = collectionJob.Id, WorkspaceId = s.Workspace, ConnectionId = collectionOp.ConnectionId.Value,
            OperationId = collectionOp.Id, OperationType = collectionOp.Type, SourceConfigurationId = s.Configuration,
            Fingerprint = new string('a', 64), ContextSnapshotJson = "{}", CommandJson = "{}", CreatedAt = now });
        await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        var before = await Snapshot(db);
        await db.Database.MigrateAsync(); Assert.Equal(before, await Snapshot(db));
        Assert.Equal(Current, (await db.Database.GetAppliedMigrationsAsync()).Last());
        Assert.Empty(await db.EmailAccounts.ToListAsync()); Assert.Empty(await db.EmailSyncStates.ToListAsync()); Assert.Empty(await db.EmailSyncJobs.ToListAsync());
        Assert.All(await db.EmailMessages.ToListAsync(), x => Assert.Null(x.EmailAccountId));
        Assert.False(db.Database.HasPendingModelChanges()); Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        await db.GetService<IMigrator>().MigrateAsync(RemoteCollectionMigrationTests.Current); Assert.Equal(before, await Snapshot(db));
        await db.Database.MigrateAsync(); Assert.Equal(before, await Snapshot(db)); Assert.False(db.Database.HasPendingModelChanges());
        // Legacy provider uniqueness is retained only for unassigned messages.
        var legacy = await db.EmailMessages.FirstAsync(); db.ChangeTracker.Clear(); legacy.Id = Guid.NewGuid(); db.Add(legacy);
        Assert.Equal(PostgresErrorCodes.UniqueViolation, Assert.IsType<PostgresException>((await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync())).InnerException).SqlState);
    }

    [Fact]
    public async Task FreshSchemaEnforcesAccountIdentityWorkspaceProviderAndActiveJobBinding()
    {
        var s = await PrepareAsync(); await using var db = Db(); var other = await SeedAsync(db);
        var account = new EmailAccount { WorkspaceId = s.Workspace, ConnectionId = Guid.NewGuid(), ProviderCode = "synthetic",
            ExternalAccountId = "one", EmailAddress = "one@example.invalid", CreatedAt = DateTimeOffset.UtcNow };
        db.Add(account); await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        EmailMessage Message(Guid? accountId = null, Guid? workspace = null, string provider = "synthetic") => new()
        {
            WorkspaceId = workspace ?? s.Workspace, EmailAccountId = accountId ?? account.Id, ProviderCode = provider,
            ExternalMessageId = "provider-message", DirectionCode = "inbound", FromAddress = "sender@example.invalid", ToAddressesJson = "[]", OccurredAt = DateTimeOffset.UtcNow
        };
        db.Add(Message()); await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        db.Add(Message()); Assert.Equal("23505", Assert.IsType<PostgresException>((await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync())).InnerException).SqlState); db.ChangeTracker.Clear();
        foreach (var bad in new[] { Message(workspace: other.Workspace), Message(provider: "different") })
        {
            bad.ExternalMessageId = Guid.NewGuid().ToString("N"); // Isolate the FK from the already tested unique index.
            db.Add(bad); Assert.Equal("23503", Assert.IsType<PostgresException>((await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync())).InnerException).SqlState); db.ChangeTracker.Clear();
        }
        var second = new EmailAccount { WorkspaceId = s.Workspace, ConnectionId = account.ConnectionId, ProviderCode = "synthetic",
            ExternalAccountId = "two", EmailAddress = "two@example.invalid", CreatedAt = DateTimeOffset.UtcNow };
        db.Add(second); await db.SaveChangesAsync(); db.Add(Message(second.Id)); await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        var now = DateTimeOffset.UtcNow;
        var op = new IntegrationOperation { Id = Guid.NewGuid(), WorkspaceId = s.Workspace, ConnectionId = account.ConnectionId,
            CommandMessageId = Guid.NewGuid(), CorrelationId = Guid.NewGuid(), Type = EmailSyncContracts.Type, Version = 1, StatusCode = "pending", CreatedAt = now };
        db.Add(op); await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        EmailSyncJob Job() => new() { WorkspaceId = s.Workspace, EmailAccountId = account.Id, ConnectionId = account.ConnectionId, IntegrationOperationId = op.Id, EnqueuedAt = now };
        foreach (var kind in new[] { "workspace", "connection", "type", "probe" })
        {
            var job = Job();
            if (kind == "workspace") job.WorkspaceId = other.Workspace;
            if (kind == "connection") job.ConnectionId = Guid.NewGuid();
            if (kind == "type") job.OperationType = "technical.probe";
            if (kind == "probe") job.IntegrationOperationId = (await new IntegrationStore(db, TimeProvider.System).CreateProbeAsync(s.Workspace, account.ConnectionId)).Id;
            db.Add(job); var error = Assert.IsType<PostgresException>((await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync())).InnerException);
            Assert.Contains(error.SqlState, new[] { "23503", "23514" }); db.ChangeTracker.Clear();
        }
        db.Add(Job()); await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        var extraOp = new IntegrationOperation { Id = Guid.NewGuid(), WorkspaceId = s.Workspace, ConnectionId = account.ConnectionId,
            CommandMessageId = Guid.NewGuid(), CorrelationId = Guid.NewGuid(), Type = EmailSyncContracts.Type, Version = 1, StatusCode = "pending", CreatedAt = now };
        db.Add(extraOp); await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        var duplicateJob = Job(); duplicateJob.IntegrationOperationId = extraOp.Id; db.Add(duplicateJob);
        Assert.Equal("23505", Assert.IsType<PostgresException>((await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync())).InnerException).SqlState); db.ChangeTracker.Clear();
        // A downgrade with imported identities is refused transactionally, not a lossy rollback.
        await Assert.ThrowsAsync<PostgresException>(() => db.GetService<IMigrator>().MigrateAsync(RemoteCollectionMigrationTests.Current));
        Assert.Equal(Current, (await db.Database.GetAppliedMigrationsAsync()).Last()); Assert.Equal(2, await db.EmailMessages.CountAsync());
        Assert.False(db.Database.HasPendingModelChanges());
    }

    private static Task<string[]> Snapshot(ProspectionCrmDbContext db) => db.Database.SqlQuery<string>($"""
        SELECT (to_jsonb(e) - 'EmailAccountId')::text AS "Value" FROM "EmailMessages" e
        UNION ALL SELECT to_jsonb(o)::text FROM "IntegrationOperations" o
        UNION ALL SELECT to_jsonb(a)::text FROM "IntegrationAttempts" a
        UNION ALL SELECT to_jsonb(r)::text FROM "IntegrationReceipts" r
        UNION ALL SELECT to_jsonb(i)::text FROM "RemoteCollectionIntents" i
        UNION ALL SELECT to_jsonb(j)::text FROM "SourceCollectionJobs" j
        ORDER BY "Value"
        """).ToArrayAsync();
}
