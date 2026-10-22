using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using ProspectionCrm.Api.Entities;
using Xunit;

namespace ProspectionCrm.Api.Tests;

public sealed class CalendarEventDeliveryConcurrencyTests(AutomationJobDatabase database) : CalendarEventDeliveryFixture(database), IClassFixture<AutomationJobDatabase>
{
    [Fact]
    public async Task ConcurrentIntentAndDispatchHaveOneAttemptWithoutSqlLockAcrossTransport()
    {
        var provider = new Provider(); using var s = await Prepare(provider); var p = await Approved(s);
        var both = await Task.WhenAll(Delivery(s, p), Delivery(s, p)); Assert.Equal(both[0].Id, both[1].Id); var d = both[0];
        provider.BeforeEffect = async () =>
        {
            await using var db = Db(); await using var tx = await db.Database.BeginTransactionAsync();
            await db.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout='2s'");
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT * FROM \"CalendarAccounts\" WHERE \"Id\"={s.Account.Id} FOR UPDATE");
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT * FROM \"Calendars\" WHERE \"Id\"={s.Calendar.Id} FOR UPDATE");
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT * FROM \"CalendarEventDeliveries\" WHERE \"Id\"={d.Id} FOR UPDATE");
        };
        var sends = await Task.WhenAll(s.Client.PostAsync(Route(d.Id,"dispatch"),null),s.Client.PostAsync(Route(d.Id,"dispatch"),null));
        Assert.Single(sends, x => x.StatusCode == HttpStatusCode.NoContent); Assert.Single(sends, x => x.StatusCode == HttpStatusCode.Conflict);
        Assert.Equal(1, provider.Calls); Assert.Equal(1, provider.Simulator.EffectCount);
        Assert.Equal("awaiting-result", (await ReadDelivery(s, d.Id)).StatusCode);
    }
    [Fact]
    public async Task CallbackAndReconcileBeforeAckDoNotRegressSuccess()
    {
        var provider = new Provider(); using var s = await Prepare(provider); var d = await Delivery(s, await Approved(s));
        provider.AfterEffect = async () =>
        {
            var replies = await Task.WhenAll(Receive(s, provider.Proof), Receive(s, provider.Proof));
            Assert.All(replies, r => Assert.Equal(HttpStatusCode.Accepted, r.StatusCode)); await Reconcile(s, d.Id);
        };
        await Dispatch(s, d.Id); Assert.Equal("succeeded", (await ReadDelivery(s, d.Id)).StatusCode);
        await using var db = Db(); Assert.Single(await db.IntegrationReceipts.Where(x => x.OperationId == d.IntegrationOperationId).ToListAsync());
    }
    [Fact]
    public async Task TwoApprovedUpdatesCanDispatchButOnlyOnePassesProviderCompareAndSwap()
    {
        var provider = new Provider(); using var s = await Prepare(provider); var target = await Target(s, provider);
        var first = await Delivery(s, await Approved(s, UpdateContent(s, target)));
        var second = await Delivery(s, await Approved(s, UpdateContent(s, target) with { Title = "Second" }));
        await Dispatch(s, first.Id); var firstProof = provider.Proof;
        await Dispatch(s, second.Id); var secondProof = provider.Proof;
        Assert.Equal("applied", firstProof.Outcome); Assert.Equal("version-conflict", secondProof.Outcome); Assert.Equal(1, provider.Simulator.EffectCount);
        await Prove(s, second.Id, secondProof); await Prove(s, first.Id, firstProof);
        Assert.Equal("conflict", (await ReadDelivery(s, second.Id)).StatusCode); Assert.Equal("succeeded", (await ReadDelivery(s, first.Id)).StatusCode);
    }
    [Theory]
    [InlineData("revision")] [InlineData("cancelled")] [InlineData("title")] [InlineData("occurrence")]
    public async Task ChangedLocalTargetBlocksDispatch(string change)
    {
        var provider = new Provider(); using var s = await Prepare(provider); var target = await Target(s, provider, true);
        var d = await Delivery(s, await Approved(s, UpdateContent(s, target))); await using var db = Db();
        var query = db.CalendarEvents.Where(x => x.Id == target.Id);
        if (change == "revision") await query.ExecuteUpdateAsync(x => x.SetProperty(e => e.ExternalRevision, 9));
        if (change == "cancelled") await query.ExecuteUpdateAsync(x => x.SetProperty(e => e.StatusCode, "cancelled"));
        if (change == "title") await query.ExecuteUpdateAsync(x => x.SetProperty(e => e.Title, "Concurrent"));
        if (change == "occurrence") await query.ExecuteUpdateAsync(x => x.SetProperty(e => e.OccurrenceId, "different"));
        Assert.Equal(HttpStatusCode.Conflict, (await s.Client.PostAsync(Route(d.Id,"dispatch"),null)).StatusCode); Assert.Equal(0, provider.Calls);
        Assert.Empty(await db.IntegrationAttempts.Where(x => x.OperationId == d.IntegrationOperationId).ToListAsync());
    }
    [Fact]
    public async Task ProviderChangeAfterCrmValidationReturnsProvenConflictAndDoesNotOverwriteCrm()
    {
        var provider = new Provider(); using var s = await Prepare(provider); var target = await Target(s, provider);
        var d = await Delivery(s, await Approved(s, UpdateContent(s, target)));
        provider.BeforeEffect = () =>
        {
            var e = new ProspectionCrm.Api.Dtos.Integrations.InboundCalendarEvent(target.ExternalEventId!, 10, "Provider concurrent", target.Description,
                target.Location,target.StartsAt,target.EndsAt,target.IsAllDay,target.TimeZoneId,"confirmed");
            provider.Simulator.Seed(s.Account.ConnectionId,s.Calendar.Id,s.Calendar.ExternalCalendarId,"synthetic",e); return Task.CompletedTask;
        };
        await Dispatch(s, d.Id); Assert.Equal("version-conflict", provider.Proof.Outcome); Assert.Equal(10, provider.Proof.ObservedRevision);
        await Prove(s, d.Id, provider.Proof); Assert.Equal("conflict", (await ReadDelivery(s, d.Id)).StatusCode); Assert.Equal(0, provider.Simulator.EffectCount);
        await using var db = Db(); Assert.Equal(target.Title, (await db.CalendarEvents.SingleAsync(x => x.Id == target.Id)).Title);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ConcurrentSyncOrLocalChangeCannotBeOverwrittenByAppliedProof(bool sync)
    {
        var provider = new Provider(); using var s = await Prepare(provider); var target = await Target(s, provider);
        var d = await Delivery(s, await Approved(s, UpdateContent(s, target))); await Dispatch(s, d.Id); var proof = provider.Proof;
        if (sync) await Import(s, proof.Event! with { Revision = proof.Event!.Revision + 1, Title = "Concurrent" });
        else { await using var db = Db(); await db.CalendarEvents.Where(x => x.Id == target.Id).ExecuteUpdateAsync(x => x.SetProperty(e => e.Title, "Concurrent")); }
        await Prove(s, d.Id, proof); var done = await ReadDelivery(s, d.Id);
        Assert.Equal("conflict", done.StatusCode); Assert.Equal("CalendarReconciliationTargetConflict", done.DiagnosticCode);
        Assert.Equal(proof.Event!.ExternalEventId, done.ExternalEventId); Assert.Equal(proof.Event.Revision, done.ExternalRevision);
        await using var check = Db(); Assert.Equal("Concurrent", (await check.CalendarEvents.SingleAsync(x => x.Id == target.Id)).Title);
        Assert.NotNull((await check.IntegrationReceipts.SingleAsync(x => x.MessageId == proof.MessageId)).ProcessedAt); Assert.Equal(1, provider.Calls);
    }
    [Fact]
    public async Task SimultaneousSyncAndReconciliationOfSameVersionAreIdempotent()
    {
        var provider = new Provider(); using var s = await Prepare(provider); var target = await Target(s, provider);
        var d = await Delivery(s, await Approved(s, UpdateContent(s, target))); await Dispatch(s, d.Id);
        Assert.Equal(HttpStatusCode.Accepted, (await Receive(s, provider.Proof)).StatusCode);
        await Task.WhenAll(Import(s, provider.Proof.Event!), Reconcile(s, d.Id));
        Assert.Equal("succeeded", (await ReadDelivery(s, d.Id)).StatusCode);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ContradictoryProofIsRetainedAndDiagnosableBeforeOrAfterSuccess(bool reconcileFirst)
    {
        var provider = new Provider(); using var s = await Prepare(provider); var d = await Delivery(s, await Approved(s)); await Dispatch(s, d.Id);
        var proof = provider.Proof; await Receive(s, proof); if (reconcileFirst) await Reconcile(s, d.Id);
        var contradiction = proof with { MessageId = Guid.NewGuid(), Outcome = "not-applied", Event = null };
        Assert.Equal(HttpStatusCode.Accepted, (await Receive(s, contradiction)).StatusCode); await Reconcile(s, d.Id);
        var done = await ReadDelivery(s, d.Id); Assert.Equal("conflict", done.StatusCode); Assert.Equal("CalendarEvidenceContradiction", done.DiagnosticCode);
        await using var db = Db(); Assert.Equal(reconcileFirst ? 1 : 0, await db.CalendarEvents.CountAsync(x => x.WorkspaceId == s.Workspace));
        Assert.Null((await db.IntegrationReceipts.SingleAsync(x => x.MessageId == contradiction.MessageId)).ProcessedAt);
        await Reconcile(s, d.Id); Assert.Equal(done.DiagnosticCode, (await ReadDelivery(s, d.Id)).DiagnosticCode);
    }
}
