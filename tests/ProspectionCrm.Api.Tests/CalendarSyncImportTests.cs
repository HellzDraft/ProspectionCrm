using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using ProspectionCrm.Api.Dtos.CalendarEvents;
using ProspectionCrm.Api.Dtos.Integrations;
using ProspectionCrm.Api.Services.Calendar;
using Xunit;
namespace ProspectionCrm.Api.Tests;
public sealed class CalendarSyncImportTests(AutomationJobDatabase database) : CalendarSyncFixture(database), IClassFixture<AutomationJobDatabase>
{
    private async Task Import(Scenario s, params InboundCalendarEvent[] events)
    {
        await NewJob(s); await Dispatch(s);
        Assert.Equal(HttpStatusCode.Accepted,(await s.Client.SendAsync(s.Host.Signed(await Body(s,events)))).StatusCode);
        await Process(s);
    }
    [Fact]
    public async Task ReplaysUpdatesAndCancellationsPreserveIdentityAndReferences()
    {
        using var s=await Prepare(); await Process(s); await Process(s);
        await using var db=Db(); var original=await db.CalendarEvents.AsNoTracking().Where(x=>x.CalendarId==s.Calendar.Id).OrderBy(x=>x.ExternalEventId).ToArrayAsync();
        Assert.All(original,x=> { Assert.Null(x.ContactId); Assert.Null(x.OpportunityId); });
        await Import(s,CalendarSyncContractTests.Event("a"),CalendarSyncContractTests.Event("b"));
        var replay=await db.CalendarSyncJobs.AsNoTracking().SingleAsync(x=>x.Id==s.Job.Id); Assert.Equal(2,replay.ExistingCount); Assert.Equal(0,replay.CreatedCount);
        await Import(s,CalendarSyncContractTests.Event("a",2) with { Title="Changed" },CalendarSyncContractTests.Event("b",2) with { Status="cancelled" });
        var after=await db.CalendarEvents.AsNoTracking().Where(x=>x.CalendarId==s.Calendar.Id).OrderBy(x=>x.ExternalEventId).ToArrayAsync();
        Assert.Equal(original.Select(x=>x.Id),after.Select(x=>x.Id)); Assert.Equal("Changed",after[0].Title); Assert.Equal("cancelled",after[1].StatusCode);
        Assert.Equal(2,(await db.CalendarSyncJobs.AsNoTracking().SingleAsync(x=>x.Id==s.Job.Id)).UpdatedCount);
        Assert.Empty(await db.Contacts.Where(x=>x.WorkspaceId==s.Workspace).ToListAsync()); Assert.Empty(await db.Opportunities.Where(x=>x.WorkspaceId==s.Workspace).ToListAsync());
        Assert.Empty(await db.Companies.Where(x=>x.WorkspaceId==s.Workspace).ToListAsync()); Assert.Empty(await db.CrmTasks.Where(x=>x.Opportunity.WorkspaceId==s.Workspace).ToListAsync());
        var read=await s.Client.GetFromJsonAsync<CalendarEventDto[]>("/api/calendar-events"); Assert.Equal(2,read!.Length);
        Assert.Contains(read,x=>x.StatusCode=="cancelled" && x.CalendarId==s.Calendar.Id);
        Assert.Equal(HttpStatusCode.OK,(await s.Client.GetAsync($"/api/calendar-events/{after[0].Id}")).StatusCode);
    }
    [Theory]
    [InlineData("same-revision")][InlineData("stale")][InlineData("recurrence-changed")][InlineData("occurrence-alias")]
    public async Task ConflictingBatchCannotPartiallyInsertOrUpdateOrAdvanceCursor(string kind)
    {
        var original=CalendarSyncContractTests.Event("a",2) with { SeriesId="series",OccurrenceId="occurrence" };
        using var s=await Prepare(messages:[original]); await Process(s);
        var bad=kind switch {
            "same-revision"=>original with { Title="Contradiction" },
            "stale"=>original with { Revision=1 },
            "recurrence-changed"=>original with { Revision=3,OccurrenceId="different" },
            _=>original with { ExternalEventId="alias",Revision=3 }
        };
        await Assert.ThrowsAsync<CalendarSyncException>(()=>Import(s,CalendarSyncContractTests.Event("new"),bad));
        await AssertState(s,"failed",1,"cursor-next","CalendarEventContentConflict");
        await using var db=Db(); var row=await db.CalendarEvents.SingleAsync(x=>x.CalendarId==s.Calendar.Id);
        Assert.Equal(2,row.ExternalRevision); Assert.Equal(original.Title,row.Title); Assert.Equal(1,(await db.CalendarSyncStates.SingleAsync(x=>x.CalendarId==s.Calendar.Id)).Revision);
    }
    [Fact]
    public async Task MultipleAccountsAndCalendarsKeepSameExternalIdentityDistinct()
    {
        using var s=await Prepare(); await Process(s);
        for(var i=0;i<2;i++)
        {
            if(i==1)
            {
                var a=await s.Client.PostAsJsonAsync("/api/calendar-accounts",new CreateCalendarAccountRequest(s.Host.Integration.ConnectionId,"synthetic","account-2",null));
                Assert.Equal(HttpStatusCode.Created,a.StatusCode); s.Account=(await a.Content.ReadFromJsonAsync<CalendarAccountDto>())!;
            }
            var response=await s.Client.PostAsJsonAsync($"/api/calendar-accounts/{s.Account.Id}/calendars",new CreateCalendarRequest("calendar-2",null,null));
            Assert.Equal(HttpStatusCode.Created,response.StatusCode); s.Calendar=(await response.Content.ReadFromJsonAsync<CalendarDto>())!;
            await Import(s,CalendarSyncContractTests.Event("a")); await AssertState(s,"succeeded",1,"cursor-next");
        }
        await using var db=Db(); Assert.Equal(3,await db.CalendarEvents.CountAsync(x=>x.WorkspaceId==s.Workspace && x.ExternalEventId=="a"));
    }
    [Theory]
    [InlineData("account")][InlineData("calendar")][InlineData("disabled")][InlineData("archived")][InlineData("cursor")][InlineData("connection")]
    public async Task ChangedFrozenContextFailsBeforeImport(string kind)
    {
        using var s=await Prepare(); await using var db=Db();
        if(kind=="account") Assert.Equal(HttpStatusCode.OK,(await s.Client.PutAsJsonAsync($"/api/calendar-accounts/{s.Account.Id}",new UpdateCalendarAccountRequest(0,"Changed","active",false))).StatusCode);
        if(kind is "calendar" or "disabled" or "archived") Assert.Equal(HttpStatusCode.OK,(await s.Client.PutAsJsonAsync($"/api/calendars/{s.Calendar.Id}",new UpdateCalendarRequest(0,"Changed","UTC",kind=="disabled"?"disabled":"active",kind=="archived"))).StatusCode);
        if(kind=="cursor") await db.CalendarSyncStates.Where(x=>x.CalendarId==s.Calendar.Id).ExecuteUpdateAsync(x=>x.SetProperty(p=>p.Revision,1));
        if(kind=="connection") s.Host.Integration.ConnectionId=Guid.NewGuid();
        await Assert.ThrowsAsync<CalendarSyncException>(()=>Process(s));
        Assert.Empty(await db.CalendarEvents.Where(x=>x.CalendarId==s.Calendar.Id).ToListAsync()); Assert.Null((await db.CalendarSyncStates.SingleAsync(x=>x.CalendarId==s.Calendar.Id)).Cursor);
    }
    [Fact]
    public async Task AllDayAndMovedRecurringOccurrenceRoundTripThroughPostgres()
    {
        var occurrence=CalendarSyncContractTests.Event("occurrence") with { SeriesId="series",OccurrenceId="original-2026-10-25",
            StartsAt=DateTimeOffset.Parse("2026-10-25T00:00:00+02:00"),EndsAt=DateTimeOffset.Parse("2026-10-26T00:00:00+01:00"),IsAllDay=true };
        using var s=await Prepare(messages:[occurrence]); await Process(s);
        await Import(s,occurrence); // Canonical UTC replay must agree with PostgreSQL and retain the local date interval.
        await using var db=Db(); var row=await db.CalendarEvents.AsNoTracking().SingleAsync(x=>x.CalendarId==s.Calendar.Id);
        Assert.Equal(25,(row.EndsAt-row.StartsAt).TotalHours); Assert.Equal("Europe/Paris",row.TimeZoneId);
        await Import(s,occurrence with { Revision=2,StartsAt=DateTimeOffset.Parse("2026-10-26T00:00:00+01:00"),EndsAt=DateTimeOffset.Parse("2026-10-27T00:00:00+01:00"),Status="cancelled" });
        var moved=await db.CalendarEvents.AsNoTracking().SingleAsync(x=>x.CalendarId==s.Calendar.Id);
        Assert.Equal(row.Id,moved.Id); Assert.Equal(row.OccurrenceId,moved.OccurrenceId); Assert.Equal("cancelled",moved.StatusCode);
    }
}
