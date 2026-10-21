using System.Text.Json.Nodes;
using ProspectionCrm.Api.Dtos.Integrations;
using Xunit;
namespace ProspectionCrm.Api.Tests;
public sealed class CalendarSyncContractTests
{
    public static InboundCalendarEvent Event(string id = "one", long revision = 1) => new(id, revision, "Synthetic meeting", "Plain text", "Virtual",
        DateTimeOffset.Parse("2026-10-21T12:00:00Z"), DateTimeOffset.Parse("2026-10-21T13:00:00Z"), false, "Europe/Paris", "confirmed");
    public static CalendarSyncResult Result(params InboundCalendarEvent[] events) => new(1, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
        "calendar.sync", new(Guid.NewGuid(), Guid.NewGuid(), "succeeded", "cursor", events));
    [Fact]
    public void ClosedContractCanonicalizesUtcOrderingAndSubMicroseconds()
    {
        var result = Result(Event("b"), Event("a") with { StartsAt = DateTimeOffset.Parse("2026-10-21T14:00:00.0000001+02:00") });
        var json = CalendarSyncContracts.Serialize(result); var parsed = CalendarSyncContracts.ParseResult(json);
        Assert.Equal("a", parsed.Payload.Events[0].ExternalEventId); Assert.Equal(TimeSpan.Zero, parsed.Payload.Events[0].StartsAt.Offset);
        Assert.Equal(0, parsed.Payload.Events[0].StartsAt.Ticks % 10); Assert.Equal(json, CalendarSyncContracts.Serialize(parsed));
    }
    [Theory]
    [InlineData("unknown")][InlineData("crm")][InlineData("recurrence-rule")][InlineData("partial-series")][InlineData("duplicate-id")]
    [InlineData("duplicate-occurrence")][InlineData("negative-revision")][InlineData("empty-id")][InlineData("long-title")][InlineData("long-body")]
    [InlineData("invalid-zone")][InlineData("windows-zone")][InlineData("end-before-start")][InlineData("zero-duration")][InlineData("no-offset")]
    [InlineData("spring-gap")][InlineData("wrong-offset")][InlineData("all-day-no-zone")][InlineData("all-day-not-midnight")]
    [InlineData("bad-status")][InlineData("failed-with-events")][InlineData("empty-guid")][InlineData("version")][InlineData("depth")][InlineData("submicro-duration")]
    public void InvalidBatchIsRejected(string kind)
    {
        var node = JsonNode.Parse(CalendarSyncContracts.Serialize(Result(Event())))!; var p = node["payload"]!; var e = p["events"]![0]!;
        switch(kind)
        {
            case "unknown": p["secret"]="sentinel"; break;
            case "crm": e["workspaceId"]=Guid.NewGuid().ToString(); break;
            case "recurrence-rule": e["rrule"]="FREQ=DAILY"; break;
            case "partial-series": e["seriesId"]="series"; break;
            case "duplicate-id": p["events"]!.AsArray().Add(e.DeepClone()); break;
            case "duplicate-occurrence": e["seriesId"]="s"; e["occurrenceId"]="o"; var second=e.DeepClone(); second["externalEventId"]="other"; p["events"]!.AsArray().Add(second); break;
            case "negative-revision": e["revision"]=-1; break;
            case "empty-id": e["externalEventId"]=" "; break;
            case "long-title": e["title"]=new string('x',501); break;
            case "long-body": e["description"]=new string('x',10001); break;
            case "invalid-zone": e["timeZoneId"]="Mars/Base"; break;
            case "windows-zone": e["timeZoneId"]="Romance Standard Time"; break;
            case "end-before-start": e["endsAt"]="2026-10-20T10:00:00Z"; break;
            case "submicro-duration": e["startsAt"]="2026-10-21T12:00:00.0000001Z"; e["endsAt"]="2026-10-21T12:00:00.0000002Z"; break;
            case "zero-duration": e["endsAt"]=e["startsAt"]!.DeepClone(); break;
            case "no-offset": e["startsAt"]="2026-10-21T12:00:00"; break;
            case "spring-gap": e["startsAt"]="2026-03-29T02:30:00+01:00"; break;
            case "wrong-offset": e["startsAt"]="2026-10-21T12:00:00+05:00"; break;
            case "all-day-no-zone": e["isAllDay"]=true; e["timeZoneId"]=null; break;
            case "all-day-not-midnight": e["isAllDay"]=true; break;
            case "bad-status": e["status"]="deleted"; break;
            case "failed-with-events": p["outcome"]="failed"; break;
            case "empty-guid": p["calendarRef"]=Guid.Empty.ToString(); break;
            case "version": node["version"]=2; break;
            case "depth": e["title"]=JsonNode.Parse("{\"a\":{\"b\":{\"c\":{\"d\":{}}}}}"); break;
        }
        Assert.Throws<IntegrationContractException>(()=>CalendarSyncContracts.ParseResult(node.ToJsonString()));
    }
    [Theory]
    [InlineData("2026-03-29T00:00:00+01:00","2026-03-30T00:00:00+02:00",23)]
    [InlineData("2026-10-25T00:00:00+02:00","2026-10-26T00:00:00+01:00",25)]
    public void AllDayHasExclusiveLocalDateEndAcrossDst(string start,string end,int hours)
    {
        var json=CalendarSyncContracts.Serialize(Result(Event() with { StartsAt=DateTimeOffset.Parse(start),EndsAt=DateTimeOffset.Parse(end),IsAllDay=true }));
        var e=CalendarSyncContracts.ParseResult(json).Payload.Events.Single(); Assert.Equal(hours,(e.EndsAt-e.StartsAt).TotalHours);
        Assert.Equal(json,CalendarSyncContracts.Serialize(CalendarSyncContracts.ParseResult(json)));
    }
    [Fact]
    public void AutumnOccurrenceUsesExplicitOffsetWithoutGuessing()
    {
        var result=Result(Event() with { StartsAt=DateTimeOffset.Parse("2026-10-25T02:30:00+02:00"),EndsAt=DateTimeOffset.Parse("2026-10-25T02:30:00+01:00"),SeriesId="series",OccurrenceId="original-start" });
        var e=CalendarSyncContracts.ParseResult(CalendarSyncContracts.Serialize(result)).Payload.Events.Single();
        Assert.Equal(TimeSpan.FromHours(1),e.EndsAt-e.StartsAt);
    }
    [Fact]
    public void DuplicatePropertiesAndOversizedBodiesAreRejected()
    {
        var json=CalendarSyncContracts.Serialize(Result(Event()));
        Assert.Throws<IntegrationContractException>(()=>CalendarSyncContracts.ParseResult(json.Replace("\"version\":1", "\"version\":1,\"version\":1")));
        Assert.Throws<IntegrationContractException>(()=>CalendarSyncContracts.ParseResult(new string(' ',CalendarSyncContracts.MaxResultBytes+1)));
        var command=new CalendarSyncCommand(1,Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),"calendar.sync",new(Guid.NewGuid(),Guid.NewGuid(),null,100));
        Assert.Equal(command,CalendarSyncContracts.ParseCommand(CalendarSyncContracts.Serialize(command)));
        Assert.Throws<IntegrationContractException>(()=>CalendarSyncContracts.Serialize(command with { Payload=command.Payload with { MaxItems=101 } }));
    }
}
