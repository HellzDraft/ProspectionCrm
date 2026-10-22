using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using ProspectionCrm.Api.Data;
using ProspectionCrm.Api.Dtos.CalendarEvents;
using ProspectionCrm.Api.Dtos.Integrations;
using ProspectionCrm.Api.Entities;
using ProspectionCrm.Api.Services.Calendar;
using Xunit;

namespace ProspectionCrm.Api.Tests;

public sealed class CalendarEventDeliveryMigrationTests : PersistentSourceIdentityFixture
{
    public const string Current = "20261022120733_Phase9641CalendarEventDeliveries";
    [Fact]
    public async Task Upgrade963PreservesPlansApprovalsEventsAndSyncEvidenceAndEmptyDownUp()
    {
        var s = await PrepareAsync(CalendarEventPlanMigrationTests.Current); await using var db = Db();
        var plan = await SeedPlan(db, s.Workspace); var now=DateTimeOffset.UtcNow;
        var op = new IntegrationOperation { Id=Guid.NewGuid(),WorkspaceId=s.Workspace,ConnectionId=plan.ConnectionId,CommandMessageId=Guid.NewGuid(),
            CorrelationId=Guid.NewGuid(),Version=1,Type="calendar.sync",StatusCode="succeeded",CreatedAt=now,CompletedAt=now };
        db.Add(op); await db.SaveChangesAsync();
        db.Add(new CalendarSyncJob { WorkspaceId=s.Workspace,CalendarId=plan.CalendarId,ConnectionId=plan.ConnectionId,TriggerTypeCode="manual",
            StatusCode="succeeded",MaxItems=10,IntegrationOperationId=op.Id,OperationType="calendar.sync",EnqueuedAt=now,StartedAt=now,FinishedAt=now });
        db.Add(new CalendarSyncState { WorkspaceId=s.Workspace,CalendarId=plan.CalendarId,Cursor="historical",Revision=8 });
        var value=new InboundCalendarEvent("historical",7,"Old",null,null,now,now.AddHours(1),false,"UTC","confirmed");
        db.Add(new CalendarEvent { WorkspaceId=s.Workspace,CalendarId=plan.CalendarId,ProviderCode="synthetic",ExternalEventId=value.ExternalEventId,
            ExternalRevision=value.Revision,ExternalContentSha256=InboundCalendarImportService.Hash(value),Title=value.Title,
            StartsAt=value.StartsAt,EndsAt=value.EndsAt,TimeZoneId=value.TimeZoneId,StatusCode=value.Status });
        db.Add(new IntegrationAttempt { OperationId=op.Id,Number=1,StatusCode="acknowledged",StartedAt=now,FinishedAt=now });
        db.Add(new IntegrationReceipt { MessageId=Guid.NewGuid(),OperationId=op.Id,CanonicalContent="{}",ContentSha256=new string('a',64),ReceivedAt=now,ProcessedAt=now });
        await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        var before=await Snapshot(db); await db.Database.MigrateAsync();
        Assert.Equal(before,await Snapshot(db)); Assert.False(db.Database.HasPendingModelChanges()); Assert.Empty(await db.CalendarEventDeliveries.ToListAsync());
        Assert.Equal(Current,(await db.Database.GetAppliedMigrationsAsync()).Last());
        await db.GetService<IMigrator>().MigrateAsync(CalendarEventPlanMigrationTests.Current); Assert.Equal(before,await Snapshot(db));
        await db.Database.MigrateAsync(); Assert.Equal(before,await Snapshot(db)); Assert.False(db.Database.HasPendingModelChanges());
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task FreshSchemaRefusesDowngradeWithIntentOrOrphanOperationEvidence(bool withDelivery)
    {
        var s=await PrepareAsync(); await using var db=Db(); Assert.False(db.Database.HasPendingModelChanges()); var p=await SeedPlan(db,s.Workspace);
        var now=DateTimeOffset.UtcNow;
        var op=new IntegrationOperation{Id=Guid.NewGuid(),WorkspaceId=s.Workspace,ConnectionId=p.ConnectionId,CommandMessageId=Guid.NewGuid(),
            CorrelationId=Guid.NewGuid(),Version=1,Type=CalendarEventSnapshots.CreateAction,StatusCode="pending",CreatedAt=now};
        db.Add(op); await db.SaveChangesAsync();
        if(withDelivery)
        {
            var d=new CalendarEventDelivery{WorkspaceId=s.Workspace,CalendarEventPlanId=p.Id,AutomationActionRequestId=p.AutomationActionRequestId!.Value,
                CalendarAccountId=p.CalendarAccountId,CalendarId=p.CalendarId,ConnectionId=p.ConnectionId,IntegrationOperationId=op.Id,
                OperationType=op.Type,ContentHash=p.ContentHash,ProviderCode="synthetic",ExternalCalendarId="calendar",IdempotencyKey="",CreatedAt=now};
            d.IdempotencyKey=$"calendar-event-{d.Id:D}"; db.Add(d); await db.SaveChangesAsync();
        }
        else
        {
            db.Add(new IntegrationReceipt{MessageId=Guid.NewGuid(),OperationId=op.Id,EvidenceNumber=1,CanonicalContent="{}",ContentSha256=new string('a',64),ReceivedAt=now});
            await db.SaveChangesAsync();
        }
        await Assert.ThrowsAsync<PostgresException>(()=>db.GetService<IMigrator>().MigrateAsync(CalendarEventPlanMigrationTests.Current));
        Assert.Equal(Current,(await db.Database.GetAppliedMigrationsAsync()).Last()); Assert.NotNull(await db.IntegrationOperations.FindAsync(op.Id));
    }
    private static async Task<CalendarEventPlan> SeedPlan(ProspectionCrmDbContext db,Guid workspace)
    {
        var now=DateTimeOffset.UtcNow; var account=new CalendarAccount{WorkspaceId=workspace,ConnectionId=Guid.NewGuid(),ProviderCode="synthetic",ExternalAccountId="account",CreatedAt=now};
        db.Add(account); await db.SaveChangesAsync();
        var calendar=new Api.Entities.Calendar{WorkspaceId=workspace,CalendarAccountId=account.Id,ConnectionId=account.ConnectionId,ProviderCode="synthetic",ExternalCalendarId="calendar",CreatedAt=now};
        db.Add(calendar); await db.SaveChangesAsync();
        var content=CalendarEventSnapshots.Normalize(new(calendar.Id,"create",null,"Approved",null,null,"2026-10-22T10:00:00Z","2026-10-22T11:00:00Z",false,"UTC"));
        var plan=new CalendarEventPlan{WorkspaceId=workspace,CalendarAccountId=account.Id,CalendarId=calendar.Id,ConnectionId=account.ConnectionId,
            OperationCode="create",ContentJson=JsonSerializer.Serialize(content,CalendarEventSnapshots.Json),ContentHash="",CreatedAt=now,
            StatusCode="approved",SubmittedRevision=0,Revision=2,SubmittedAt=now,ApprovedAt=now};
        plan.ContentHash=CalendarEventSnapshots.Hash(CalendarEventSnapshots.From(plan));
        var request=new AutomationActionRequest{WorkspaceId=workspace,DecisionRequirementCode="manual",ActionTypeCode=CalendarEventSnapshots.CreateAction,
            ActionCategoryCode="calendar",ActionPlanJson=CalendarEventSnapshots.Serialize(CalendarEventSnapshots.From(plan)),ContentFingerprint=plan.ContentHash,
            RuleFingerprint=plan.ContentHash,RequestedReasonCode="manual",RequestedAt=now,StatusCode="approved",DecidedAt=now,
            DecidedByUserId=await db.Workspaces.Where(x=>x.Id==workspace).Select(x=>x.OwnerUserId).SingleAsync()};
        db.Add(request); await db.SaveChangesAsync(); plan.AutomationActionRequestId=request.Id; db.Add(plan); await db.SaveChangesAsync(); return plan;
    }
    private static async Task<string[]> Snapshot(ProspectionCrmDbContext db)
    {
        var tables=await db.Database.SqlQuery<string>($"""
            SELECT tablename AS "Value" FROM pg_tables WHERE schemaname='public'
            AND tablename NOT IN ('__EFMigrationsHistory','CalendarEventDeliveries') ORDER BY tablename
            """).ToArrayAsync();
        var rows=new List<string>();
        foreach(var table in tables)
        {
            var quoted=table.Replace("\"","\"\"");
#pragma warning disable EF1003
            rows.AddRange((await db.Database.SqlQueryRaw<string>("SELECT to_jsonb(t)::text AS \"Value\" FROM \""+quoted+"\" t ORDER BY 1").ToArrayAsync()).Select(x=>table+":"+x));
#pragma warning restore EF1003
        }
        return rows.ToArray();
    }
}
