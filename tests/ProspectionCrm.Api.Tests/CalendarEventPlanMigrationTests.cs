using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using ProspectionCrm.Api.Data;
using ProspectionCrm.Api.Entities;
using Xunit;
namespace ProspectionCrm.Api.Tests;
public sealed class CalendarEventPlanMigrationTests : PersistentSourceIdentityFixture
{
    public const string Current = "20261021144859_Phase963CalendarEventPlans";
    [Fact]
    public async Task Upgrade961PreservesEveryHistoricalRowAndEmptyDownUpIsSafe()
    {
        var s = await PrepareAsync(CalendarSyncMigrationTests.Current); await using var db = Db();
        var now = DateTimeOffset.UtcNow;
        var account = new CalendarAccount { WorkspaceId = s.Workspace, ConnectionId = Guid.NewGuid(), ProviderCode = "synthetic", ExternalAccountId = "account", CreatedAt = now };
        db.Add(account); await db.SaveChangesAsync();
        var calendar = new Api.Entities.Calendar { WorkspaceId = s.Workspace, CalendarAccountId = account.Id, ConnectionId = account.ConnectionId, ProviderCode = "synthetic", ExternalCalendarId = "calendar", CreatedAt = now };
        db.Add(calendar); await db.SaveChangesAsync();
        db.Add(new CalendarSyncState { WorkspaceId = s.Workspace, CalendarId = calendar.Id, Cursor = "retained", Revision = 3 });
        db.Add(new CalendarEvent { WorkspaceId = s.Workspace, CalendarId = calendar.Id, ProviderCode = "synthetic", ExternalEventId = "imported",
            ExternalRevision = 2, ExternalContentSha256 = new string('a',64), StatusCode = "confirmed", Title = "Historical", StartsAt = now, EndsAt = now.AddHours(1) });
        db.Add(new CalendarEvent { WorkspaceId = s.Workspace, ProviderCode = "legacy", Title = "Legacy", StartsAt = now, EndsAt = now });
        db.Add(new AutomationActionRequest { WorkspaceId=s.Workspace,DecisionRequirementCode="manual",ActionTypeCode="email.send",ActionCategoryCode="email",
            ActionPlanJson="{}",ContentFingerprint=new string('a',64),RuleFingerprint=new string('a',64),RequestedReasonCode="historical",RequestedAt=now });
        await db.SaveChangesAsync();
        var emailAccount = new EmailAccount { WorkspaceId = s.Workspace, ConnectionId = Guid.NewGuid(), ProviderCode = "synthetic",
            ExternalAccountId = "email-account", EmailAddress = "synthetic@example.invalid", CreatedAt = now };
        var operation = new IntegrationOperation { WorkspaceId = s.Workspace, ConnectionId = emailAccount.ConnectionId, CommandMessageId = Guid.NewGuid(),
            CorrelationId = Guid.NewGuid(), Type = "email.send", Version = 1, StatusCode = "awaiting-result", CreatedAt = now };
        db.AddRange(emailAccount, operation); await db.SaveChangesAsync();
        var request = await db.AutomationActionRequests.SingleAsync();
        var plan = new EmailSendPlan { WorkspaceId = s.Workspace, EmailAccountId = emailAccount.Id, ConnectionId = emailAccount.ConnectionId,
            FromAddressSnapshot = emailAccount.EmailAddress, ToAddressesJson = "[\"synthetic@example.invalid\"]", CcAddressesJson = "[]",
            BodyText = "Historical", ContentHash = new string('a',64), CreatedAt = now, StatusCode = "pending-approval",
            AutomationActionRequestId = request.Id, SubmittedAt = now, SubmittedRevision = 0, Revision = 1 };
        db.Add(plan); await db.SaveChangesAsync();
        var deliveryId = Guid.NewGuid();
        db.Add(new EmailSendDelivery { Id = deliveryId, WorkspaceId = s.Workspace, EmailSendPlanId = plan.Id, AutomationActionRequestId = request.Id,
            EmailAccountId = emailAccount.Id, ConnectionId = emailAccount.ConnectionId, IntegrationOperationId = operation.Id,
            ContentHash = plan.ContentHash, IdempotencyKey = "email-send-" + deliveryId, ProviderCode = "synthetic", StatusCode = "send-unknown", CreatedAt = now });
        db.Add(new IntegrationAttempt { OperationId = operation.Id, Number = 1, StatusCode = "unknown", StartedAt = now, FinishedAt = now });
        db.Add(new IntegrationReceipt { EvidenceNumber = 1, MessageId = Guid.NewGuid(), OperationId = operation.Id, CanonicalContent = "{}", ContentSha256 = new string('a',64), ReceivedAt = now });
        await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        var before = await Snapshot(db); await db.Database.MigrateAsync();
        Assert.Equal(before, await Snapshot(db)); Assert.Equal(CalendarEventDeliveryMigrationTests.Current, (await db.Database.GetAppliedMigrationsAsync()).Last());
        Assert.False(db.Database.HasPendingModelChanges()); Assert.Empty(await db.CalendarEventPlans.ToListAsync());
        await db.GetService<IMigrator>().MigrateAsync(CalendarSyncMigrationTests.Current); Assert.Equal(before, await Snapshot(db));
        await db.Database.MigrateAsync(); Assert.Equal(before, await Snapshot(db)); Assert.False(db.Database.HasPendingModelChanges());
    }
    [Fact]
    public async Task FreshSchemaRefusesLossOfCalendarRequestsEvenWithoutPlans()
    {
        var s = await PrepareAsync(); await using var db = Db();
        Assert.False(db.Database.HasPendingModelChanges());
        db.Add(new AutomationActionRequest { WorkspaceId=s.Workspace,DecisionRequirementCode="manual",ActionTypeCode="calendar.event.create",ActionCategoryCode="calendar",
            ActionPlanJson="{}",ContentFingerprint=new string('a',64),RuleFingerprint=new string('a',64),RequestedReasonCode="manual",RequestedAt=DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<PostgresException>(() => db.GetService<IMigrator>().MigrateAsync(CalendarSyncMigrationTests.Current));
        Assert.Equal(Current, (await db.Database.GetAppliedMigrationsAsync()).Last()); Assert.Single(await db.AutomationActionRequests.ToListAsync());
    }
    private static async Task<string[]> Snapshot(ProspectionCrmDbContext db)
    {
        var tables = await db.Database.SqlQuery<string>($"""
            SELECT tablename AS "Value" FROM pg_tables WHERE schemaname='public'
            AND tablename NOT IN ('__EFMigrationsHistory','CalendarEventPlans') ORDER BY tablename
            """).ToArrayAsync();
        var rows = new List<string>();
        foreach (var table in tables)
        {
            var quoted = table.Replace("\"", "\"\"");
#pragma warning disable EF1003
            rows.AddRange((await db.Database.SqlQueryRaw<string>("SELECT to_jsonb(t)::text AS \"Value\" FROM \"" + quoted + "\" t ORDER BY 1").ToArrayAsync()).Select(x => table + ":" + x));
#pragma warning restore EF1003
        }
        return rows.ToArray();
    }
}
