using System.Net;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using ProspectionCrm.Api.Dtos.Integrations;
using ProspectionCrm.Api.Entities;
using Xunit;

namespace ProspectionCrm.Api.Tests;

public sealed class CalendarEventDeliverySecurityTests(AutomationJobDatabase database) : CalendarEventDeliveryFixture(database), IClassFixture<AutomationJobDatabase>
{
    [Fact]
    public async Task LogicalConnectionChangeCannotDispatchAnExistingIntent()
    {
        var provider = new Provider(); using var s = await Prepare(provider); var d = await Delivery(s, await Approved(s));
        using var changed = await Restart(s, provider, false);
        Assert.Equal(HttpStatusCode.Conflict, (await changed.Client.PostAsync(Route(d.Id,"dispatch"),null)).StatusCode);
        Assert.Equal(0, provider.Calls);
    }
    [Fact]
    public async Task OpenAutomationCircuitBlocksCalendarExecutionWithoutChangingManualPolicy()
    {
        var provider = new Provider(); using var s = await Prepare(provider); var d = await Delivery(s, await Approved(s)); var second = await Approved(s);
        await using var db = Db(); var now = DateTimeOffset.UtcNow;
        var rule = new AutomationRule { WorkspaceId=s.Workspace,Name="Synthetic circuit history",TriggerTypeCode="manual",ActionTypeCode="test" };
        db.Add(rule); await db.SaveChangesAsync();
        for (var i=1; i<=3; i++)
        {
            var job = new AutomationJob { WorkspaceId=s.Workspace,AutomationRuleId=rule.Id,TriggerTypeCode="manual",ActionCategoryCode="general",StatusCode="failed",
                CreatedAt=now,AvailableAt=now,CompletedAt=now,AttemptCount=1 };
            db.Add(job); await db.SaveChangesAsync();
            db.Add(new AutomationExecution { WorkspaceId=s.Workspace,AutomationRuleId=rule.Id,AutomationJobId=job.Id,AttemptNumber=1,StatusCode="failed",
                IsAutomaticAttempt=true,OutcomeSequence=i,TriggeredAt=now,StartedAt=now,FinishedAt=now,ReasonCode="synthetic-failure" });
            await db.SaveChangesAsync();
        }
        Assert.Equal(HttpStatusCode.Conflict, (await s.Client.PostAsync(Route(d.Id,"dispatch"),null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await s.Client.PostAsync($"/api/calendar-event-deliveries/from-plan/{second.Id}",null)).StatusCode);
        Assert.Equal(0, provider.Calls);
    }
    [Theory]
    [InlineData("account")] [InlineData("calendar")] [InlineData("archived")] [InlineData("settings")]
    [InlineData("decision")] [InlineData("owner")] [InlineData("snapshot")] [InlineData("action")] [InlineData("future-decision")]
    public async Task AuthorizationIsRevalidatedBeforeIntentAndBeforeDispatch(string change)
    {
        var provider = new Provider(); using var s = await Prepare(provider); var p = await Approved(s); var d = await Delivery(s, p);
        // A second plan verifies creation against the same invalidated environment.
        var second = await Approved(s);
        await using var db = Db();
        if (change == "account") await db.CalendarAccounts.Where(x => x.Id == s.Account.Id).ExecuteUpdateAsync(x => x.SetProperty(a => a.StatusCode,"disabled"));
        if (change == "calendar") await db.Calendars.Where(x => x.Id == s.Calendar.Id).ExecuteUpdateAsync(x => x.SetProperty(a => a.StatusCode,"disabled"));
        if (change == "archived") await db.Calendars.Where(x => x.Id == s.Calendar.Id).ExecuteUpdateAsync(x => x.SetProperty(a => a.ArchivedAt,DateTimeOffset.UtcNow));
        if (change == "settings") await db.AutomationRuntimeSettings.Where(x => x.WorkspaceId == s.Workspace).ExecuteUpdateAsync(x => x.SetProperty(a => a.IsEnabled,false));
        var requests = db.AutomationActionRequests.Where(x => x.WorkspaceId == s.Workspace);
        if (change == "decision") await requests.ExecuteUpdateAsync(x => x.SetProperty(a => a.StatusCode,"rejected"));
        if (change == "future-decision") await requests.ExecuteUpdateAsync(x => x.SetProperty(a => a.DecidedAt,DateTimeOffset.UtcNow.AddDays(1)));
        if (change == "owner") { var outsider = new UserAccount { Email="outsider@example.invalid" }; db.Add(outsider); await db.SaveChangesAsync();
            await requests.ExecuteUpdateAsync(x => x.SetProperty(a => a.DecidedByUserId,outsider.Id)); }
        if (change == "snapshot") await requests.ExecuteUpdateAsync(x => x.SetProperty(a => a.ContentFingerprint,new string('0',64)));
        if (change == "action") await requests.ExecuteUpdateAsync(x => x.SetProperty(a => a.ActionTypeCode,"calendar.event.update"));
        Assert.Equal(HttpStatusCode.Conflict, (await s.Client.PostAsync(Route(d.Id,"dispatch"),null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await s.Client.PostAsync($"/api/calendar-event-deliveries/from-plan/{second.Id}",null)).StatusCode);
        Assert.Equal(0,provider.Calls); Assert.Empty(await db.IntegrationAttempts.Where(x => x.OperationId == d.IntegrationOperationId).ToListAsync());
    }
    [Fact]
    public async Task MissingSettingsPendingPlanAndUnavailableTransportFailClosed()
    {
        using var s = await Prepare(); var pending = await Submit(s, await Create(s));
        Assert.Equal(HttpStatusCode.Conflict, (await s.Client.PostAsync($"/api/calendar-event-deliveries/from-plan/{pending.Id}",null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Decide(s, pending)).StatusCode); var d = await Delivery(s, pending);
        Assert.Equal(HttpStatusCode.Conflict, (await s.Client.PostAsync(Route(d.Id,"dispatch"),null)).StatusCode);
        Assert.Equal("pending", (await ReadDelivery(s,d.Id)).StatusCode);
        var other = await Approved(s); await using var db = Db(); await db.AutomationRuntimeSettings.Where(x => x.WorkspaceId == s.Workspace).ExecuteDeleteAsync();
        Assert.Equal(HttpStatusCode.Conflict, (await s.Client.PostAsync($"/api/calendar-event-deliveries/from-plan/{other.Id}",null)).StatusCode);
    }
    [Theory]
    [InlineData("wrong-workspace")] [InlineData("wrong-connection")] [InlineData("revoked")]
    public async Task CallbackRequiresValidPersistedScopeAndHmac(string key)
    {
        var provider = new Provider(); using var s = await Prepare(provider); var d = await Delivery(s, await Approved(s)); await Dispatch(s,d.Id);
        Assert.Contains((await Receive(s,provider.Proof,key)).StatusCode,new[]{HttpStatusCode.Forbidden,HttpStatusCode.Unauthorized});
        Assert.Equal("awaiting-result",(await ReadDelivery(s,d.Id)).StatusCode);
    }
    [Fact]
    public async Task HumanRoutesRequireCookieCsrfAndWorkspaceAndHmacCannotDispatch()
    {
        var provider = new Provider(); using var s = await Prepare(provider); var p = await Approved(s); var d = await Delivery(s,p);
        using var anon = s.Host.Client();
        foreach(var route in new[]{ $"/api/calendar-event-deliveries/from-plan/{p.Id}", Route(d.Id,"dispatch"),Route(d.Id,"reconcile") })
            Assert.Equal(HttpStatusCode.Unauthorized,(await anon.PostAsync(route,null)).StatusCode);
        using var other = await Prepare(provider); Assert.Equal(HttpStatusCode.NotFound,(await other.Client.GetAsync(Route(d.Id))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,(await other.Client.PostAsync(Route(d.Id,"dispatch"),null)).StatusCode);
        var signed = s.Host.Signed(Encoding.UTF8.GetBytes("{}"),target:Route(d.Id,"dispatch"));
        Assert.Equal(HttpStatusCode.Unauthorized,(await anon.SendAsync(signed)).StatusCode);
        s.Client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        Assert.Equal(HttpStatusCode.BadRequest,(await s.Client.PostAsync(Route(d.Id,"dispatch"),null)).StatusCode); Assert.Equal(0,provider.Calls);
    }
    [Theory]
    [InlineData("operation")] [InlineData("correlation")] [InlineData("command")] [InlineData("hash")] [InlineData("calendar")] [InlineData("provider")]
    public async Task FullEvidenceCorrelationCannotBeSubstituted(string field)
    {
        var provider = new Provider(); using var s = await Prepare(provider); var d = await Delivery(s,await Approved(s)); await Dispatch(s,d.Id);
        var proof=provider.Proof;
        proof=field switch {"operation"=>proof with{OperationId=Guid.NewGuid()},"correlation"=>proof with{CorrelationId=Guid.NewGuid()},
            "command"=>proof with{CommandMessageId=Guid.NewGuid()},"hash"=>proof with{ContentHash=new string('0',64)},
            "calendar"=>proof with{ExternalCalendarId="other"},_=>proof with{ProviderCode="other"}};
        Assert.Equal(HttpStatusCode.Conflict,(await Receive(s,proof)).StatusCode);
        await using var db=Db(); Assert.Empty(await db.IntegrationReceipts.Where(x=>x.OperationId==d.IntegrationOperationId).ToListAsync());
    }
    [Theory]
    [InlineData("content")] [InlineData("time")] [InlineData("identity")]
    public async Task InvalidProviderProofIsKeptWithoutBusinessMutation(string field)
    {
        var provider=new Provider(); using var s=await Prepare(provider); var target=await Target(s,provider);
        var d=await Delivery(s,await Approved(s,UpdateContent(s,target))); await Dispatch(s,d.Id);
        var proof=provider.Proof;
        proof=field switch {"content"=>proof with{Event=proof.Event! with{Title="Unapproved"}},
            "time"=>proof with{ProvenAt=DateTimeOffset.UtcNow.AddDays(-1)},_=>proof with{Event=proof.Event! with{ExternalEventId="other"}}};
        await Prove(s,d.Id,proof); Assert.Equal("conflict",(await ReadDelivery(s,d.Id)).StatusCode);
        await using var db=Db(); Assert.Equal(target.Title,(await db.CalendarEvents.SingleAsync(x=>x.Id==target.Id)).Title);
        Assert.Null((await db.IntegrationReceipts.SingleAsync(x=>x.MessageId==proof.MessageId)).ProcessedAt);
    }
    [Fact]
    public async Task ReplayWithChangedContentFailsWithoutReplacingStoredEvidence()
    {
        var provider=new Provider(); using var s=await Prepare(provider); var d=await Delivery(s,await Approved(s)); await Dispatch(s,d.Id);
        var proof=provider.Proof; await Receive(s,proof);
        Assert.Equal(HttpStatusCode.Conflict,(await Receive(s,proof with{Event=proof.Event! with{Title="Different"}})).StatusCode);
        await Reconcile(s,d.Id); Assert.Equal("succeeded",(await ReadDelivery(s,d.Id)).StatusCode);
    }
    [Fact]
    public async Task QuotaIsCheckedUnderWorkspaceLockAtCreationAndDispatch()
    {
        var provider=new Provider(); using var s=await Prepare(provider); var first=await Delivery(s,await Approved(s));
        var secondPlan=await Approved(s); var second=await Delivery(s,secondPlan); var third=await Approved(s);
        await using var db=Db(); await db.AutomationRuntimeSettings.Where(x=>x.WorkspaceId==s.Workspace).ExecuteUpdateAsync(x=>x.SetProperty(a=>a.MaxExecutionsPerMinute,1));
        await Dispatch(s,first.Id);
        Assert.Equal(HttpStatusCode.Conflict,(await s.Client.PostAsync(Route(second.Id,"dispatch"),null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict,(await s.Client.PostAsync($"/api/calendar-event-deliveries/from-plan/{third.Id}",null)).StatusCode);
        Assert.Equal(1,provider.Calls);
    }
    [Fact]
    public async Task SqlGuardsRejectIdentityMutationRetryAndEvidenceDeletion()
    {
        var provider=new Provider(); using var s=await Prepare(provider); var d=await Delivery(s,await Approved(s));
        await using var db=Db();
        await Assert.ThrowsAsync<PostgresException>(()=>db.AutomationActionRequests.Where(x=>x.Id==d.AutomationActionRequestId).ExecuteUpdateAsync(x=>x.SetProperty(a=>a.ActionCategoryCode,"general")));
        await Assert.ThrowsAsync<PostgresException>(()=>db.CalendarEventDeliveries.Where(x=>x.Id==d.Id).ExecuteUpdateAsync(x=>x.SetProperty(a=>a.ContentHash,new string('0',64))));
        await Assert.ThrowsAsync<PostgresException>(()=>db.CalendarEventDeliveries.Where(x=>x.Id==d.Id).ExecuteDeleteAsync());
        await Assert.ThrowsAsync<PostgresException>(()=>db.IntegrationOperations.Where(x=>x.Id==d.IntegrationOperationId).ExecuteUpdateAsync(x=>x.SetProperty(a=>a.CommandMessageId,Guid.NewGuid())));
        await Dispatch(s,d.Id); await Receive(s,provider.Proof);
        await Assert.ThrowsAsync<PostgresException>(()=>db.IntegrationReceipts.Where(x=>x.OperationId==d.IntegrationOperationId).ExecuteDeleteAsync());
        await Assert.ThrowsAsync<PostgresException>(()=>db.IntegrationAttempts.Where(x=>x.OperationId==d.IntegrationOperationId).ExecuteDeleteAsync());
        db.IntegrationAttempts.Add(new(){OperationId=d.IntegrationOperationId,Number=2,StatusCode="started",StartedAt=DateTimeOffset.UtcNow});
        await Assert.ThrowsAsync<DbUpdateException>(()=>db.SaveChangesAsync()); db.ChangeTracker.Clear();
        await Prove(s,d.Id,provider.Proof);
        await Assert.ThrowsAsync<PostgresException>(()=>db.CalendarEventDeliveries.Where(x=>x.Id==d.Id).ExecuteUpdateAsync(x=>x.SetProperty(a=>a.ExternalEventId,"forged")));
    }
}
