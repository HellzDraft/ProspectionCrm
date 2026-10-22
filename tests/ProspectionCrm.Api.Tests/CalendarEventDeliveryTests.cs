using System.Net;
using Microsoft.EntityFrameworkCore;
using ProspectionCrm.Api.Dtos.Integrations;
using Xunit;

namespace ProspectionCrm.Api.Tests;

public sealed class CalendarEventDeliveryTests(AutomationJobDatabase database) : CalendarEventDeliveryFixture(database), IClassFixture<AutomationJobDatabase>
{
    [Fact]
    public async Task ProvenNonApplicationIsTerminalWithoutChangingCrm()
    {
        var provider = new Provider(); using var s = await Prepare(provider); var target = await Target(s,provider);
        var d = await Delivery(s,await Approved(s,UpdateContent(s,target)));
        provider.BeforeEffect = () =>
        {
            provider.Simulator.Seed(s.Account.ConnectionId,s.Calendar.Id,s.Calendar.ExternalCalendarId,"synthetic",
                new(target.ExternalEventId!,target.ExternalRevision!.Value,target.Title,target.Description,target.Location,
                    target.StartsAt,target.EndsAt,target.IsAllDay,target.TimeZoneId,"cancelled")); return Task.CompletedTask;
        };
        await Dispatch(s,d.Id); Assert.Equal("not-applied",provider.Proof.Outcome); await Prove(s,d.Id,provider.Proof);
        Assert.Equal("failed",(await ReadDelivery(s,d.Id)).StatusCode); Assert.Equal(0,provider.Simulator.EffectCount);
        Assert.Equal(HttpStatusCode.Conflict,(await s.Client.PostAsync(Route(d.Id,"dispatch"),null)).StatusCode);
        await using var db=Db(); Assert.Equal("confirmed",(await db.CalendarEvents.SingleAsync(x=>x.Id==target.Id)).StatusCode);
    }
    [Fact]
    public async Task CreateIntentAckProofAndReplayPreserveApprovedPlanAndRealSimulatorIdentity()
    {
        var provider = new Provider(); using var s = await Prepare(provider); var refs = await References(s);
        var p = await Approved(s, Content(s) with { OpportunityId = refs.Opportunity, ContactId = refs.Contact }); var d = await Delivery(s, p);
        Assert.Equal(d.Id, (await Delivery(s, p)).Id); await Dispatch(s, d.Id);
        Assert.Equal("awaiting-result", (await ReadDelivery(s, d.Id)).StatusCode);
        await using var db = Db(); Assert.Empty(await db.CalendarEvents.Where(x => x.WorkspaceId == s.Workspace).ToListAsync());
        Assert.Equal("disabled", provider.Command!.Notifications);
        var proof = provider.Proof; await Prove(s, d.Id, proof); await Prove(s, d.Id, proof);
        await Prove(s, d.Id, proof with { MessageId = Guid.NewGuid() });
        var done = await ReadDelivery(s, d.Id); Assert.Equal("succeeded", done.StatusCode);
        var e = await db.CalendarEvents.SingleAsync(x => x.WorkspaceId == s.Workspace);
        Assert.Equal(proof.Event!.ExternalEventId, e.ExternalEventId); Assert.Equal(proof.Event.Revision, e.ExternalRevision);
        Assert.Equal(s.Calendar.Id, e.CalendarId); Assert.Equal("synthetic", e.ProviderCode); Assert.Equal(p.Content.Content.Title, e.Title);
        Assert.Equal(refs.Opportunity, e.OpportunityId); Assert.Equal(refs.Contact, e.ContactId);
        Assert.Equal("approved", (await Read(s, p.Id)).StatusCode); Assert.Equal(d.Id, (await Delivery(s, p)).Id);
        Assert.Equal("succeeded", (await db.IntegrationOperations.SingleAsync(x => x.Id == d.IntegrationOperationId)).StatusCode);
        Assert.All(await db.IntegrationReceipts.Where(x => x.OperationId == d.IntegrationOperationId).ToListAsync(), x => Assert.NotNull(x.ProcessedAt));
        Assert.Equal(HttpStatusCode.Conflict, (await s.Client.PostAsync(Route(d.Id, "dispatch"), null)).StatusCode);
        Assert.Equal(1, provider.Calls); Assert.Equal(1, provider.Simulator.EffectCount); Assert.Equal(0, s.Network.Calls);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task UpdateKeepsIdReferencesAndOccurrenceAndSyncOfExactProofIsIdempotent(bool syncFirst)
    {
        var provider = new Provider(); using var s = await Prepare(provider); var target = await Target(s, provider, true);
        var p = await Approved(s, UpdateContent(s, target)); var d = await Delivery(s, p); await Dispatch(s, d.Id);
        Assert.Equal(target.ExternalRevision, provider.Command!.Precondition!.ExpectedRevision);
        Assert.Equal(target.ExternalContentSha256, provider.Command.Precondition.TargetContentHash);
        if (syncFirst) await Import(s, provider.Proof.Event!);
        await Prove(s, d.Id, provider.Proof); await Reconcile(s, d.Id);
        await using var db = Db(); var actual = await db.CalendarEvents.SingleAsync(x => x.Id == target.Id);
        Assert.Equal(target.Id, (await ReadDelivery(s, d.Id)).CalendarEventId); Assert.Equal(target.ExternalEventId, actual.ExternalEventId);
        Assert.Equal(target.SeriesId, actual.SeriesId); Assert.Equal(target.OccurrenceId, actual.OccurrenceId);
        Assert.Equal(target.ContactId, actual.ContactId); Assert.Equal(target.OpportunityId, actual.OpportunityId);
        Assert.Equal(target.ExternalRevision + 1, actual.ExternalRevision); Assert.Equal("succeeded", (await ReadDelivery(s, d.Id)).StatusCode);
        Assert.Equal(d.Id, (await Delivery(s, p)).Id); Assert.Equal(1, provider.Calls);
    }
    [Fact]
    public async Task CreateAlreadyImportedBySyncReusesTheEvent()
    {
        var provider = new Provider(); using var s = await Prepare(provider); var d = await Delivery(s, await Approved(s));
        await Dispatch(s, d.Id); await Import(s, provider.Proof.Event!); await Prove(s, d.Id, provider.Proof);
        await using var db = Db(); Assert.Single(await db.CalendarEvents.Where(x => x.WorkspaceId == s.Workspace).ToListAsync());
        Assert.Equal("succeeded", (await ReadDelivery(s, d.Id)).StatusCode);
    }
    [Theory]
    [InlineData("timeout")] [InlineData("disconnect")] [InlineData("http-error")] [InlineData("crash-before")]
    public async Task UnknownNeverRetriesAndLateProofCanFinish(string mode)
    {
        var provider = new Provider { Mode = mode }; using var s = await Prepare(provider); var d = await Delivery(s, await Approved(s));
        await Dispatch(s, d.Id); Assert.Equal("operation-unknown", (await ReadDelivery(s, d.Id)).StatusCode);
        await Reconcile(s, d.Id); Assert.Equal("operation-unknown", (await ReadDelivery(s, d.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await s.Client.PostAsync(Route(d.Id, "dispatch"), null)).StatusCode);
        if (mode != "crash-before") { await Prove(s, d.Id, provider.Proof); Assert.Equal("succeeded", (await ReadDelivery(s, d.Id)).StatusCode); }
        Assert.Equal(1, provider.Calls);
        await using var db = Db(); Assert.Single(await db.IntegrationAttempts.Where(x => x.OperationId == d.IntegrationOperationId).ToListAsync());
    }
    [Fact]
    public async Task AmbiguousEvidenceThenLateProofIsDurable()
    {
        var provider = new Provider(); using var s = await Prepare(provider); var d = await Delivery(s, await Approved(s)); await Dispatch(s, d.Id);
        var unknown = provider.Proof with { MessageId = Guid.NewGuid(), Outcome = "unknown", Event = null, ProvenAt = null };
        await Prove(s, d.Id, unknown); Assert.Equal("operation-unknown", (await ReadDelivery(s, d.Id)).StatusCode);
        await Prove(s, d.Id, provider.Proof); Assert.Equal("succeeded", (await ReadDelivery(s, d.Id)).StatusCode);
        await using var db = Db(); Assert.Equal(2, await db.IntegrationReceipts.CountAsync(x => x.OperationId == d.IntegrationOperationId && x.ProcessedAt != null));
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task CrashCheckpointSurvivesNewServiceAndProviderLookupDoesNotRepeatEffect(bool afterEffect)
    {
        var provider = new Provider(); using var s = await Prepare(provider); var d = await Delivery(s, await Approved(s));
        var (command, _) = await BeginWithoutTransport(s, d);
        if (afterEffect) await provider.DispatchAsync(command, default);
        using var restarted = await Restart(s, provider); restarted.Host.Clock.Now = DateTimeOffset.UtcNow.AddMinutes(3);
        await Reconcile(restarted, d.Id); Assert.Equal("operation-unknown", (await ReadDelivery(restarted, d.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await restarted.Client.PostAsync(Route(d.Id, "dispatch"), null)).StatusCode);
        if (afterEffect) { await Prove(restarted, d.Id, provider.Simulator.FindProof(d.IdempotencyKey)!); Assert.Equal("succeeded", (await ReadDelivery(restarted, d.Id)).StatusCode); }
        Assert.Equal(afterEffect ? 1 : 0, provider.Calls);
    }
    [Theory]
    [InlineData("2026-03-29T00:00:00+01:00", "2026-03-30T00:00:00+02:00", 23)]
    [InlineData("2026-10-25T00:00:00+02:00", "2026-10-26T00:00:00+01:00", 25)]
    public async Task AllDayDstAndExclusiveEndUseInboundCalendarRules(string start, string end, int hours)
    {
        var provider = new Provider(); using var s = await Prepare(provider);
        var d = await Delivery(s, await Approved(s, Content(s) with { StartsAt = start, EndsAt = end, IsAllDay = true }));
        await Dispatch(s, d.Id); await Prove(s, d.Id, provider.Proof);
        await using var db = Db(); var e = await db.CalendarEvents.SingleAsync(x => x.WorkspaceId == s.Workspace);
        Assert.True(e.IsAllDay); Assert.Equal(hours, (e.EndsAt - e.StartsAt).TotalHours); Assert.Equal("Europe/Paris", e.TimeZoneId);
    }
    [Fact]
    public async Task CommitFailureRollsBackReceiptStateAndBusinessMutationThenReconcileRecovers()
    {
        var provider = new Provider(); using var s = await Prepare(provider); var d = await Delivery(s, await Approved(s)); await Dispatch(s, d.Id);
        var proof = provider.Proof; Assert.Equal(HttpStatusCode.Accepted, (await Receive(s, proof)).StatusCode);
        await using var db = Db(); await db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION fail_calendar_commit() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'synthetic failure'; END $$;
            CREATE CONSTRAINT TRIGGER fail_calendar_commit AFTER INSERT ON "CalendarEvents" DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION fail_calendar_commit();
            """);
        try
        {
            await Assert.ThrowsAsync<Npgsql.PostgresException>(() => s.Client.PostAsync(Route(d.Id, "reconcile"), null));
            Assert.Empty(await db.CalendarEvents.Where(x => x.WorkspaceId == s.Workspace).ToListAsync());
            Assert.Null((await db.IntegrationReceipts.AsNoTracking().SingleAsync(x => x.MessageId == proof.MessageId)).ProcessedAt);
            Assert.Equal("result-received", (await ReadDelivery(s, d.Id)).StatusCode);
        }
        finally { await db.Database.ExecuteSqlRawAsync("DROP TRIGGER fail_calendar_commit ON \"CalendarEvents\"; DROP FUNCTION fail_calendar_commit();"); }
        await Reconcile(s, d.Id); await Reconcile(s, d.Id); Assert.Equal("succeeded", (await ReadDelivery(s, d.Id)).StatusCode); Assert.Equal(1, provider.Calls);
    }
}
