using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using ProspectionCrm.Api.Data;
using ProspectionCrm.Api.Entities;
using Xunit;
namespace ProspectionCrm.Api.Tests;
public sealed class CalendarSyncMigrationTests : PersistentSourceIdentityFixture
{
    public const string Current="20261021110428_Phase961InboundCalendarSync";
    [Fact]
    public async Task UpgradeFrom9541PreservesAllHistoricalRowsAndEmptyDownUpIsSafe()
    {
        var s=await PrepareAsync(EmailSendDeliveryMigrationTests.Current); await using var db=Db();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "CalendarEvents" ("Id","WorkspaceId","OpportunityId","ProviderCode","ExternalEventId","Title","StartsAt","EndsAt","IsAllDay","CreatedAt")
            VALUES ({Guid.NewGuid()},{s.Workspace},{s.Opportunity},'legacy','external','Historical',statement_timestamp(),statement_timestamp(),false,statement_timestamp());
            INSERT INTO "EmailMessages" ("Id","WorkspaceId","ProviderCode","ExternalMessageId","DirectionCode","FromAddress","ToAddressesJson","OccurredAt","CreatedAt")
            VALUES ({Guid.NewGuid()},{s.Workspace},'legacy','mail','outbound','synthetic@example.invalid','[]',statement_timestamp(),statement_timestamp());
            """);
        var now=DateTimeOffset.UtcNow;
        var operation=new IntegrationOperation { WorkspaceId=s.Workspace,ConnectionId=Guid.NewGuid(),CommandMessageId=Guid.NewGuid(),
            CorrelationId=Guid.NewGuid(),Type="email.send",Version=1,StatusCode="awaiting-result",CreatedAt=now };
        db.Add(operation); await db.SaveChangesAsync();
        db.Add(new IntegrationAttempt { OperationId=operation.Id,Number=1,StatusCode="unknown",StartedAt=now,FinishedAt=now });
        db.Add(new IntegrationReceipt { EvidenceNumber=1,MessageId=Guid.NewGuid(),OperationId=operation.Id,CanonicalContent="{\"synthetic\":true}",ContentSha256=new string('a',64),ReceivedAt=now });
        db.Add(new AutomationActionRequest { WorkspaceId=s.Workspace,DecisionRequirementCode="approval-required",StatusCode="pending",ActionTypeCode="email.send",
            ActionCategoryCode="email",ActionPlanJson="{}",ContentFingerprint=new string('a',64),RuleFingerprint=new string('a',64),RequestedReasonCode="historical",RequestedAt=now });
        await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        var before=await Snapshot(db); await db.Database.MigrateAsync(); Assert.Equal(before,await Snapshot(db));
        Assert.Equal(CalendarEventDeliveryMigrationTests.Current,(await db.Database.GetAppliedMigrationsAsync()).Last()); Assert.False(db.Database.HasPendingModelChanges());
        Assert.Null((await db.CalendarEvents.SingleAsync()).CalendarId);
        await db.GetService<IMigrator>().MigrateAsync(EmailSendDeliveryMigrationTests.Current); Assert.Equal(before,await Snapshot(db));
        await db.Database.MigrateAsync(); Assert.Equal(before,await Snapshot(db)); Assert.False(db.Database.HasPendingModelChanges());
    }
    [Fact]
    public async Task FreshSchemaEnforcesCalendarWorkspaceProviderIdentityAndRefusesLossyDown()
    {
        var s=await PrepareAsync(); await using var db=Db(); var other=await SeedAsync(db);
        var account=new CalendarAccount { WorkspaceId=s.Workspace,ConnectionId=Guid.NewGuid(),ProviderCode="synthetic",ExternalAccountId="account",CreatedAt=DateTimeOffset.UtcNow };
        db.Add(account); await db.SaveChangesAsync();
        var calendar=new ProspectionCrm.Api.Entities.Calendar { WorkspaceId=s.Workspace,CalendarAccountId=account.Id,ConnectionId=account.ConnectionId,ProviderCode=account.ProviderCode,ExternalCalendarId="calendar",CreatedAt=DateTimeOffset.UtcNow };
        db.Add(calendar); await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        CalendarEvent Event(Guid workspace,string provider)=>new() { WorkspaceId=workspace,CalendarId=calendar.Id,ProviderCode=provider,ExternalEventId="external",Title="Synthetic",
            StartsAt=DateTimeOffset.UtcNow,EndsAt=DateTimeOffset.UtcNow.AddHours(1),ExternalRevision=1,ExternalContentSha256=new string('a',64),StatusCode="confirmed" };
        foreach(var bad in new[]{Event(other.Workspace,"synthetic"),Event(s.Workspace,"other")})
        {
            db.Add(bad); Assert.Equal("23503",Assert.IsType<PostgresException>((await Assert.ThrowsAsync<DbUpdateException>(()=>db.SaveChangesAsync())).InnerException).SqlState); db.ChangeTracker.Clear();
        }
        db.Add(Event(s.Workspace,"synthetic")); await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        db.Add(Event(s.Workspace,"synthetic")); Assert.Equal("23505",Assert.IsType<PostgresException>((await Assert.ThrowsAsync<DbUpdateException>(()=>db.SaveChangesAsync())).InnerException).SqlState); db.ChangeTracker.Clear();
        await Assert.ThrowsAsync<PostgresException>(()=>db.GetService<IMigrator>().MigrateAsync(EmailSendDeliveryMigrationTests.Current));
        Assert.Equal(Current,(await db.Database.GetAppliedMigrationsAsync()).Last()); Assert.Single(await db.CalendarEvents.ToListAsync()); Assert.False(db.Database.HasPendingModelChanges());
    }
    // All pre-existing application tables, including requests, receipts and email evidence, compare byte-for-byte as JSON.
    private static async Task<string[]> Snapshot(ProspectionCrmDbContext db)
    {
        var tables=await db.Database.SqlQuery<string>($"""
            SELECT tablename AS "Value" FROM pg_tables WHERE schemaname='public' AND tablename NOT IN
            ('__EFMigrationsHistory','CalendarAccounts','Calendars','CalendarSyncStates','CalendarSyncJobs') ORDER BY tablename
            """).ToArrayAsync();
        var result=new List<string>();
        foreach(var table in tables)
        {
            var expression=table=="CalendarEvents" ? "to_jsonb(t) - ARRAY['CalendarId','ExternalRevision','ExternalContentSha256','StatusCode','SeriesId','OccurrenceId']" : "to_jsonb(t)";
            var sql=$"SELECT ({expression})::text AS \"Value\" FROM \"{table.Replace("\"","\"\"")}\" t ORDER BY 1";
            result.AddRange((await db.Database.SqlQueryRaw<string>(sql).ToArrayAsync()).Select(x=>table+":"+x));
        }
        return result.ToArray();
    }
}
