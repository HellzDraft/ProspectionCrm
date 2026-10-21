using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using ProspectionCrm.Api.Data;
using ProspectionCrm.Api.Entities;
using Xunit;

namespace ProspectionCrm.Api.Tests;

public sealed class EmailSendPlanMigrationTests : PersistentSourceIdentityFixture
{
    public const string Current = "20261016123347_Phase953EmailSendPlans";

    [Fact]
    public async Task UpgradeFrom951AndEmptyFeatureDownUpPreserveEveryHistoricalRow()
    {
        var s = await PrepareAsync(EmailSyncMigrationTests.Current); await using var db = Db();
        var account = new EmailAccount { WorkspaceId = s.Workspace, ConnectionId = Guid.NewGuid(), ProviderCode = "synthetic",
            ExternalAccountId = "migration", EmailAddress = "migration@example.invalid", CreatedAt = DateTimeOffset.UtcNow };
        var rule = new AutomationRule { WorkspaceId = s.Workspace, Name = "Legacy", TriggerTypeCode = "manual", ActionTypeCode = "create-crm-task" };
        db.AddRange(account, rule); await db.SaveChangesAsync();
        var now = DateTimeOffset.UtcNow;
        var job = new AutomationJob { WorkspaceId = s.Workspace, AutomationRuleId = rule.Id, TriggerTypeCode = "manual",
            ActionCategoryCode = "general", StatusCode = "awaiting-approval", CreatedAt = now, AvailableAt = now };
        var operation = new IntegrationOperation { WorkspaceId = s.Workspace, ConnectionId = account.ConnectionId,
            CommandMessageId = Guid.NewGuid(), CorrelationId = Guid.NewGuid(), Type = "email.sync", Version = 1, StatusCode = "pending", CreatedAt = now };
        db.AddRange(job, operation); await db.SaveChangesAsync();
        db.AddRange(new EmailSyncState { WorkspaceId = s.Workspace, EmailAccountId = account.Id, Cursor = "retained-cursor", Revision = 3, UpdatedAt = now },
            new EmailSyncJob { WorkspaceId = s.Workspace, EmailAccountId = account.Id, ConnectionId = account.ConnectionId,
                IntegrationOperationId = operation.Id, EnqueuedAt = now },
            new EmailMessage { WorkspaceId = s.Workspace, EmailAccountId = account.Id, ProviderCode = "synthetic", ExternalMessageId = "historical",
                DirectionCode = "inbound", FromAddress = "from@example.invalid", ToAddressesJson = "[]", OccurredAt = now });
        await db.SaveChangesAsync();
        var requestId = Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "AutomationActionRequests" ("Id","WorkspaceId","AutomationJobId","AutomationRuleId","DecisionRequirementCode",
                "StatusCode","ActionTypeCode","ActionCategoryCode","ActionPlanJson","RuleFingerprint","RequestedReasonCode","RequestedAt")
            VALUES ({requestId},{s.Workspace},{job.Id},{rule.Id},'manual','pending','create-crm-task','general',jsonb_build_object(),repeat('a',64),'legacy',now());
            INSERT INTO "AutomationExecutions" ("Id","WorkspaceId","AutomationRuleId","StatusCode","TriggeredAt")
            VALUES ({Guid.NewGuid()},{s.Workspace},{rule.Id},'skipped',now());
            """);
        var before = await Snapshot(db);
        await db.Database.MigrateAsync(); Assert.Equal(before, await Snapshot(db));
        Assert.Equal(EmailSendDeliveryMigrationTests.Current, (await db.Database.GetAppliedMigrationsAsync()).Last()); Assert.Empty(await db.EmailSendPlans.ToListAsync());
        Assert.False(db.Database.HasPendingModelChanges()); Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        await db.GetService<IMigrator>().MigrateAsync(EmailSyncMigrationTests.Current); Assert.Equal(before, await Snapshot(db));
        await db.Database.MigrateAsync(); Assert.Equal(before, await Snapshot(db));
        Assert.False(db.Database.HasPendingModelChanges());
    }
    [Fact]
    public async Task FreshSchemaAndPopulatedDowngradeGuardPreserveEmailAuthorizationHistory()
    {
        var s = await PrepareAsync(); await using var db = Db();
        Assert.Equal(EmailSendDeliveryMigrationTests.Current, (await db.Database.GetAppliedMigrationsAsync()).Last()); Assert.False(db.Database.HasPendingModelChanges());
        var account = new EmailAccount { WorkspaceId = s.Workspace, ConnectionId = Guid.NewGuid(), ProviderCode = "synthetic",
            ExternalAccountId = "guard", EmailAddress = "guard@example.invalid", CreatedAt = DateTimeOffset.UtcNow };
        db.Add(account); await db.SaveChangesAsync();
        db.Add(new EmailSendPlan { WorkspaceId = s.Workspace, EmailAccountId = account.Id, ConnectionId = account.ConnectionId,
            FromAddressSnapshot = account.EmailAddress, ToAddressesJson = "[\"to@example.invalid\"]", CcAddressesJson = "[]",
            BodyText = "Retain this draft", ContentHash = new string('a', 64), CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<PostgresException>(() => db.GetService<IMigrator>().MigrateAsync(EmailSyncMigrationTests.Current));
        Assert.Equal(Current, (await db.Database.GetAppliedMigrationsAsync()).Last()); Assert.Single(await db.EmailSendPlans.ToListAsync());
    }

    private static async Task<string[]> Snapshot(ProspectionCrmDbContext db)
    {
        var tables = await db.Database.SqlQuery<string>($"""
            SELECT tablename AS "Value" FROM pg_tables WHERE schemaname = 'public'
                AND tablename NOT IN ('__EFMigrationsHistory','EmailSendDeliveries','EmailSendPlans') ORDER BY tablename
            """).ToArrayAsync();
        var rows = new List<string>();
        foreach (var table in tables)
        {
            // Names come only from PostgreSQL metadata, quoted as identifiers.
            var quoted = table.Replace("\"", "\"\"");
#pragma warning disable EF1003 // PostgreSQL metadata identifiers, escaped above; no request values.
            rows.AddRange((await db.Database.SqlQueryRaw<string>("SELECT (to_jsonb(t) - 'ContentFingerprint' - 'EvidenceNumber')::text AS \"Value\" FROM \"" + quoted + "\" t ORDER BY \"Value\"")
                .ToArrayAsync()).Select(x => table + ":" + x));
#pragma warning restore EF1003
        }
        return rows.ToArray();
    }
}
