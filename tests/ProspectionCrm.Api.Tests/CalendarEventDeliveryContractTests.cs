using System.Text.Json.Nodes;
using ProspectionCrm.Api.Dtos.CalendarEvents;
using ProspectionCrm.Api.Dtos.Integrations;
using ProspectionCrm.Api.Services.Calendar;
using Xunit;

namespace ProspectionCrm.Api.Tests;

public sealed class CalendarEventDeliveryContractTests
{
    private static CalendarEventCommand Command()
    {
        var c = CalendarEventSnapshots.Normalize(new(Guid.NewGuid(),"create",null,"Approved","Body","Place",
            "2026-10-25T00:00:00+02:00","2026-10-26T00:00:00+01:00",true,"Europe/Paris"));
        var s = new CalendarEventSnapshot(1,Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),0,c,null);
        var id=Guid.NewGuid(); return new(1,Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),CalendarEventSnapshots.CreateAction,id,
            $"calendar-event-{id:D}",CalendarEventSnapshots.Hash(s),"synthetic","calendar",s,null,"disabled");
    }
    private static async Task<CalendarEventResult> Proof()
    {
        var command=Command(); var simulator=new InMemoryCalendarEventTransport(); await simulator.DispatchAsync(command,default);
        return simulator.FindProof(command.IdempotencyKey)!;
    }
    [Fact]
    public async Task ClosedVersionedRoundtripAndProviderKeyLedgerAreIdempotent()
    {
        var c=Command(); Assert.Equal(c,CalendarEventContracts.ParseCommand(CalendarEventContracts.Serialize(c)));
        var simulator=new InMemoryCalendarEventTransport(); await Task.WhenAll(simulator.DispatchAsync(c,default),simulator.DispatchAsync(c,default));
        var proof=simulator.FindProof(c.IdempotencyKey)!; Assert.Equal(proof,CalendarEventContracts.ParseResult(CalendarEventContracts.Serialize(proof)));
        Assert.StartsWith("simulated-event-",proof.Event!.ExternalEventId); Assert.Equal(1,simulator.EffectCount);
        await Assert.ThrowsAsync<CalendarSyncException>(()=>simulator.DispatchAsync(c with{MessageId=Guid.NewGuid()},default));
    }
    [Theory]
    [InlineData("attendees")] [InlineData("participants")] [InlineData("sendUpdates")] [InlineData("notifyAttendees")]
    [InlineData("token")] [InlineData("callbackUrl")] [InlineData("workspaceId")]
    public void UnknownCommandAndNestedContentFieldsAreRejected(string name)
    {
        var json=JsonNode.Parse(CalendarEventContracts.Serialize(Command()))!; json[name]="forbidden";
        Assert.Throws<IntegrationContractException>(()=>CalendarEventContracts.ParseCommand(json.ToJsonString()));
        json=JsonNode.Parse(CalendarEventContracts.Serialize(Command()))!; json["snapshot"]!["content"]![name]="forbidden";
        Assert.Throws<IntegrationContractException>(()=>CalendarEventContracts.ParseCommand(json.ToJsonString()));
    }
    [Theory]
    [InlineData("version")] [InlineData("type")] [InlineData("hash")] [InlineData("notifications")]
    [InlineData("id")] [InlineData("missing")] [InlineData("precondition")] [InlineData("oversized")]
    public void InvalidCommandShapeOrMeaningIsRejected(string change)
    {
        var root=JsonNode.Parse(CalendarEventContracts.Serialize(Command()))!;
        switch(change)
        {
            case "version": root["version"]=2; break;
            case "type": root["type"]="calendar.event.delete"; break;
            case "hash": root["contentHash"]=new string('a',64); break;
            case "notifications": root["notifications"]="enabled"; break;
            case "id": root["operationId"]=Guid.Empty.ToString(); break;
            case "missing": root.AsObject().Remove("notifications"); break;
            case "precondition": root["precondition"]=new JsonObject(); break;
            case "oversized": root["externalCalendarId"]=new string('x',70000); break;
        }
        Assert.Throws<IntegrationContractException>(()=>CalendarEventContracts.ParseCommand(root.ToJsonString()));
    }
    [Theory]
    [InlineData("duplicate")] [InlineData("nested-duplicate")] [InlineData("unknown")]
    [InlineData("no-offset")] [InlineData("no-time-offset")] [InlineData("conflict-without-version")]
    [InlineData("unknown-with-proof")] [InlineData("reversed-date")] [InlineData("invalid-type")] [InlineData("diagnostic")]
    public async Task InvalidResultsAreRejected(string change)
    {
        var json=CalendarEventContracts.Serialize(await Proof()); var root=JsonNode.Parse(json)!;
        if(change=="duplicate") json=json.Insert(1,"\"outcome\":\"applied\",");
        else if(change=="nested-duplicate") json=json.Replace("\"event\":{","\"event\":{\"title\":\"duplicate\",");
        else
        {
            switch(change)
            {
                case "unknown": root["attendees"]=new JsonArray(); break;
                case "no-offset": root["event"]!["startsAt"]="2026-10-25T00:00:00"; break;
                case "no-time-offset": root["provenAt"]="2026-10-25T00:00:00"; break;
                case "conflict-without-version": root["outcome"]="version-conflict"; root["event"]=null; break;
                case "unknown-with-proof": root["outcome"]="unknown"; break;
                case "reversed-date": root["event"]!["endsAt"]=root["event"]!["startsAt"]!.DeepClone(); break;
                case "invalid-type": root["event"]!["revision"]="2"; break;
                case "diagnostic": root["diagnosticCode"]=new string('x',65); break;
            }
            json=root.ToJsonString();
        }
        Assert.Throws<IntegrationContractException>(()=>CalendarEventContracts.ParseResult(json));
    }
    [Fact]
    public async Task MissingTargetIsProvenNotAppliedAndSameRevisionDifferentContentCannotOverwrite()
    {
        var create=Command(); var simulator=new InMemoryCalendarEventTransport(); await simulator.DispatchAsync(create,default);
        var old=simulator.FindProof(create.IdempotencyKey)!.Event!;
        var target=new CalendarEventTarget(Guid.NewGuid(),"synthetic",old.ExternalEventId,old.Revision,InboundCalendarImportService.Hash(old),
            old.Title,old.Description,old.Location,old.StartsAt,old.EndsAt,old.IsAllDay,old.TimeZoneId,old.Status,null,null,null,null);
        var snapshot=create.Snapshot with{PlanId=Guid.NewGuid(),Content=create.Snapshot.Content with{OperationCode="update",TargetCalendarEventId=target.Id},Target=target};
        var delivery=Guid.NewGuid(); var update=create with{MessageId=Guid.NewGuid(),OperationId=Guid.NewGuid(),DeliveryId=delivery,
            IdempotencyKey=$"calendar-event-{delivery:D}",Type=CalendarEventSnapshots.UpdateAction,Snapshot=snapshot,ContentHash=CalendarEventSnapshots.Hash(snapshot),
            Precondition=new(snapshot.Content.CalendarId,create.ExternalCalendarId,old.ExternalEventId,old.Revision,target.ContentHash)};
        var empty=new InMemoryCalendarEventTransport(); await empty.DispatchAsync(update,default);
        Assert.Equal("not-applied",empty.FindProof(update.IdempotencyKey)!.Outcome); Assert.Equal(0,empty.EffectCount);
        simulator.Seed(snapshot.ConnectionId,snapshot.Content.CalendarId,create.ExternalCalendarId,"synthetic",old with{Title="concurrent"});
        await simulator.DispatchAsync(update,default); Assert.Equal("not-applied",simulator.FindProof(update.IdempotencyKey)!.Outcome); Assert.Equal(1,simulator.EffectCount);
    }
}
