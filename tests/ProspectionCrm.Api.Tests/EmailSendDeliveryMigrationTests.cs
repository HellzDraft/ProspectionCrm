using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using ProspectionCrm.Api.Entities;
using ProspectionCrm.Api.Data;
using ProspectionCrm.Api.Services.Email;
using Xunit;

namespace ProspectionCrm.Api.Tests;

public sealed class EmailSendDeliveryMigrationTests : PersistentSourceIdentityFixture
{
    public const string Current = "20261021075256_Phase9541EmailSendDeliveries";
    [Fact]
    public async Task Upgrade953PreservesRowsAndEmptyDownUpHasNoPendingModelChanges()
    {
        var s=await PrepareAsync(EmailSendPlanMigrationTests.Current); await using var db=Db();
        var account=new EmailAccount {WorkspaceId=s.Workspace,ConnectionId=Guid.NewGuid(),ProviderCode="synthetic",ExternalAccountId="preserved",EmailAddress="sender@example.invalid",CreatedAt=DateTimeOffset.UtcNow};
        db.Add(account); await db.SaveChangesAsync();
        var now=DateTimeOffset.UtcNow;
        var p=new EmailSendPlan {WorkspaceId=s.Workspace,EmailAccountId=account.Id,ConnectionId=account.ConnectionId,FromAddressSnapshot=account.EmailAddress,
            ToAddressesJson="[\"recipient@example.invalid\"]",CcAddressesJson="[]",BodyText="Keep approval",ContentHash="",CreatedAt=now,
            SubmittedAt=now,ApprovedAt=now,StatusCode="approved",SubmittedRevision=0,Revision=2};
        p.ContentHash=EmailSendSnapshots.Hash(EmailSendSnapshots.From(p));
        var request=new AutomationActionRequest {WorkspaceId=s.Workspace,DecisionRequirementCode="approval-required",StatusCode="approved",ActionTypeCode="email.send",
            ActionCategoryCode="email",ActionPlanJson=EmailSendSnapshots.Serialize(EmailSendSnapshots.From(p)),ContentFingerprint=p.ContentHash,RuleFingerprint=p.ContentHash,
            RequestedReasonCode="preserved",RequestedAt=now,DecidedAt=now,DecidedByUserId=(await db.Workspaces.FindAsync(s.Workspace))!.OwnerUserId};
        p.AutomationActionRequestId=request.Id; db.AddRange(request,p); await db.SaveChangesAsync();
        var before=await Snapshot(db);
        await db.Database.MigrateAsync(); Assert.False(db.Database.HasPendingModelChanges());
        Assert.Equal(CalendarEventPlanMigrationTests.Current,(await db.Database.GetAppliedMigrationsAsync()).Last()); Assert.Empty(await db.EmailSendDeliveries.ToListAsync());
        Assert.Equal(before,await Snapshot(db));
        await db.GetService<IMigrator>().MigrateAsync(EmailSendPlanMigrationTests.Current); await db.Database.MigrateAsync();
        Assert.Equal(before,await Snapshot(db));
        Assert.False(db.Database.HasPendingModelChanges()); Assert.Empty(await db.Database.GetPendingMigrationsAsync());
    }
    private static async Task<string[]> Snapshot(ProspectionCrmDbContext db)
    {
        var tables=await db.Database.SqlQuery<string>($"""
            SELECT tablename AS "Value" FROM pg_tables WHERE schemaname='public'
                AND tablename NOT IN ('__EFMigrationsHistory','EmailSendDeliveries') ORDER BY tablename
            """).ToListAsync();
        var rows=new List<string>();
        foreach(var table in tables)
        {
            var quoted=table.Replace("\"","\"\"");
#pragma warning disable EF1003 // Identifiers originate in PostgreSQL metadata and are escaped.
            rows.AddRange((await db.Database.SqlQueryRaw<string>("SELECT (to_jsonb(t) - 'EvidenceNumber')::text AS \"Value\" FROM \""+quoted+"\" t ORDER BY \"Value\"").ToListAsync()).Select(x=>table+":"+x));
#pragma warning restore EF1003
        }
        return rows.ToArray();
    }
    [Fact]
    public async Task FreshSchemaAndDowngradeGuardRetainSendOperationEvidence()
    {
        var s=await PrepareAsync(); await using var db=Db(); Assert.False(db.Database.HasPendingModelChanges());
        var op=new IntegrationOperation {Id=Guid.NewGuid(),WorkspaceId=s.Workspace,ConnectionId=Guid.NewGuid(),CommandMessageId=Guid.NewGuid(),
            CorrelationId=Guid.NewGuid(),Version=1,Type="email.send",StatusCode="pending",CreatedAt=DateTimeOffset.UtcNow};
        db.Add(op); await db.SaveChangesAsync();
        await Assert.ThrowsAsync<PostgresException>(()=>db.GetService<IMigrator>().MigrateAsync(EmailSendPlanMigrationTests.Current));
        Assert.Equal(Current,(await db.Database.GetAppliedMigrationsAsync()).Last()); Assert.NotNull(await db.IntegrationOperations.FindAsync(op.Id));
    }
}
