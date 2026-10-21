using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using ProspectionCrm.Api.Dtos.CalendarEvents;
using ProspectionCrm.Api.Services.Calendar;
using Xunit;

namespace ProspectionCrm.Api.Tests;

public sealed class CalendarAccountSecurityTests(AutomationJobDatabase database) : CalendarSyncFixture(database), IClassFixture<AutomationJobDatabase>
{
    [Fact]
    public async Task AccountCreationIsUniqueAndUpdatesAreRevisionProtectedWithoutSecrets()
    {
        using var s = await Prepare(dispatch: false, receive: false);
        var request = new CreateCalendarAccountRequest(s.Host.Integration.ConnectionId, "synthetic", "account-1", null);
        Assert.Equal(HttpStatusCode.Conflict, (await s.Client.PostAsJsonAsync("/api/calendar-accounts", request)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await s.Client.PostAsJsonAsync("/api/calendar-accounts", request with { ConnectionId = Guid.NewGuid(), ExternalAccountId = "other" })).StatusCode);
        var update = new UpdateCalendarAccountRequest(0, "Display", "disabled", false);
        var response = await s.Client.PutAsJsonAsync($"/api/calendar-accounts/{s.Account.Id}", update); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = (await response.Content.ReadFromJsonAsync<CalendarAccountDto>())!;
        Assert.Equal("account-1", updated.ExternalAccountId); Assert.Equal(1, updated.Revision);
        Assert.Equal(HttpStatusCode.Conflict, (await s.Client.PutAsJsonAsync($"/api/calendar-accounts/{s.Account.Id}", update)).StatusCode);
        var json = await s.Client.GetStringAsync("/api/calendar-accounts");
        foreach (var field in new[] { "secret", "password", "token", "credential", "canonicalContent" }) Assert.DoesNotContain(field, json, StringComparison.OrdinalIgnoreCase);
        await using var db = Db(); Assert.Equal(1, await db.CalendarSyncStates.CountAsync(x => x.CalendarId == s.Calendar.Id));
    }

    [Theory]
    [InlineData("disabled")][InlineData("revoked")][InlineData("archived")]
    public async Task InactiveAccountsCannotEnqueueOrDispatch(string status)
    {
        using var s = await Prepare(dispatch: false, receive: false);
        var update = new UpdateCalendarAccountRequest(0, null, status == "archived" ? "active" : status, status == "archived");
        Assert.Equal(HttpStatusCode.OK, (await s.Client.PutAsJsonAsync($"/api/calendar-accounts/{s.Account.Id}", update)).StatusCode);
        Assert.Equal("CalendarAccountUnavailable", (await Assert.ThrowsAsync<CalendarSyncException>(() => Dispatch(s))).Message);
        await AssertState(s, "failed", 0, null, "CalendarAccountUnavailable");
        Assert.Equal(HttpStatusCode.Conflict, (await s.Client.PostAsJsonAsync($"/api/calendars/{s.Calendar.Id}/sync-jobs", new EnqueueCalendarSyncRequest())).StatusCode);
        var restore = update with { StatusCode = "active", Archived = false, Revision = 1 };
        Assert.Equal(status == "revoked" ? HttpStatusCode.Conflict : HttpStatusCode.OK,
            (await s.Client.PutAsJsonAsync($"/api/calendar-accounts/{s.Account.Id}", restore)).StatusCode);
    }

    [Fact]
    public async Task HumanCookieMachineHmacAndCsrfAreSeparated()
    {
        using var s = await Prepare(receive: false); using var anonymous = s.Host.Client();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/calendar-accounts")).StatusCode);
        using var machineHumanRoute = s.Host.Signed([], target: $"/api/calendar-sync-jobs/{s.Job.Id}/process-result");
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.SendAsync(machineHumanRoute)).StatusCode);
        var content = new StringContent(Encoding.UTF8.GetString(s.Body), Encoding.UTF8, "application/json");
        Assert.Equal(HttpStatusCode.Unauthorized, (await s.Client.PostAsync("/api/integrations/results", content)).StatusCode);
        s.Client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Client.PostAsync($"/api/calendar-sync-jobs/{s.Job.Id}/process-result", null)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Client.PostAsync($"/api/calendar-sync-jobs/{s.Job.Id}/cancel", null)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Client.PostAsJsonAsync("/api/calendar-accounts", new { })).StatusCode);
        foreach (var key in new[] { "wrong-workspace", "wrong-connection" })
            Assert.Equal(HttpStatusCode.Forbidden, (await anonymous.SendAsync(s.Host.Signed(s.Body, key))).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await anonymous.SendAsync(s.Host.Signed(s.Body))).StatusCode);
        foreach (var key in new[] { "wrong-workspace", "wrong-connection" })
            Assert.Equal(HttpStatusCode.Forbidden, (await anonymous.SendAsync(s.Host.Signed(s.Body, key))).StatusCode);
    }

    [Fact]
    public async Task WorkspaceIsolationIncludesReadsUpdatesEnqueueProcessingAndReplay()
    {
        using var s = await Prepare(); await Process(s);
        using var other = await SecurityTestHost.CreateAsync(Database); using var client = other.Client(); await SecurityTestHost.LoginAsync(client);
        Assert.Empty((await client.GetFromJsonAsync<CalendarAccountDto[]>("/api/calendar-accounts"))!);
        Assert.Empty((await client.GetFromJsonAsync<CalendarEventDto[]>($"/api/calendar-events?emailAccountId={s.Account.Id}"))!);
        foreach (var url in new[] { $"/api/calendar-sync-jobs/{s.Job.Id}" }) Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync($"/api/calendar-sync-jobs/{s.Job.Id}/process-result", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync($"/api/calendars/{s.Calendar.Id}/sync-jobs", new EnqueueCalendarSyncRequest())).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PutAsJsonAsync($"/api/calendar-accounts/{s.Account.Id}", new UpdateCalendarAccountRequest(0, null, "active", false))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(other.Signed(s.Body))).StatusCode);
        await using var db = Db(); var message = await db.CalendarEvents.FirstAsync(x => x.CalendarId == s.Calendar.Id);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/calendar-events/{message.Id}")).StatusCode);
    }

    [Theory]
    [InlineData("accessToken")][InlineData("refreshToken")][InlineData("password")][InlineData("workspaceId")][InlineData("credential")]
    public async Task UnknownAccountPropertiesAreRejected(string property)
    {
        using var s = await Prepare(dispatch: false, receive: false);
        var node = new JsonObject { ["connectionId"] = s.Host.Integration.ConnectionId.ToString(), ["providerCode"] = "synthetic",
            ["externalAccountId"] = "other", ["displayName"] = null, [property] = "sensitive-sentinel" };
        var response = await s.Client.PostAsJsonAsync("/api/calendar-accounts", node); Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain("sensitive-sentinel", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task SignedOversizeBodyAndChangedReplayAreRejected()
    {
        using var s = await Prepare();
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await s.Client.SendAsync(s.Host.Signed(new byte[512 * 1024 + 1]))).StatusCode);
        var node = JsonNode.Parse(s.Body)!; node["payload"]!["events"]![0]!["description"] = "Changed";
        Assert.Equal(HttpStatusCode.Conflict, (await s.Client.SendAsync(s.Host.Signed(Encoding.UTF8.GetBytes(node.ToJsonString())))).StatusCode);
        await AssertState(s, "result-received", 0, null);
    }

    [Theory]
    [InlineData("revision")][InlineData("archived")]
    public async Task AccountUpdateRequiresExplicitRevisionAndArchiveDecision(string missing)
    {
        using var s = await Prepare(dispatch: false, receive: false);
        var node = new JsonObject { ["displayName"] = null, ["statusCode"] = "active", ["archived"] = false, ["revision"] = 0 };
        node.Remove(missing);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Client.PutAsJsonAsync($"/api/calendar-accounts/{s.Account.Id}", node)).StatusCode);
        await using var db = Db(); Assert.Equal(0, (await db.CalendarAccounts.SingleAsync(x => x.Id == s.Account.Id)).Revision);
    }
    [Fact]
    public async Task CalendarAdministrationIsRevisionProtectedArchivedWithoutDeletionAndGetDoesNotMutate()
    {
        using var s=await Prepare(dispatch:false,receive:false); await using var db=Db();
        var before=await db.Database.SqlQuery<string>($"SELECT to_jsonb(c)::text AS \"Value\" FROM \"Calendars\" c WHERE \"Id\"={s.Calendar.Id}").SingleAsync();
        Assert.Equal(HttpStatusCode.OK,(await s.Client.GetAsync($"/api/calendars/{s.Calendar.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK,(await s.Client.GetAsync($"/api/calendar-accounts/{s.Account.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK,(await s.Client.GetAsync($"/api/calendar-sync-jobs/{s.Job.Id}")).StatusCode);
        Assert.Equal(before,await db.Database.SqlQuery<string>($"SELECT to_jsonb(c)::text AS \"Value\" FROM \"Calendars\" c WHERE \"Id\"={s.Calendar.Id}").SingleAsync());
        var request=new UpdateCalendarRequest(0,"Archived","UTC","disabled",true);
        Assert.Equal(HttpStatusCode.OK,(await s.Client.PutAsJsonAsync($"/api/calendars/{s.Calendar.Id}",request)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict,(await s.Client.PutAsJsonAsync($"/api/calendars/{s.Calendar.Id}",request)).StatusCode);
        Assert.Equal("CalendarUnavailable",(await Assert.ThrowsAsync<CalendarSyncException>(()=>Dispatch(s))).Message);
        Assert.Equal(HttpStatusCode.Conflict,(await s.Client.PostAsJsonAsync($"/api/calendars/{s.Calendar.Id}/sync-jobs",new EnqueueCalendarSyncRequest())).StatusCode);
        Assert.NotNull((await db.Calendars.SingleAsync(x=>x.Id==s.Calendar.Id)).ArchivedAt);
        s.Client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        Assert.Equal(HttpStatusCode.BadRequest,(await s.Client.PutAsJsonAsync($"/api/calendars/{s.Calendar.Id}",request with { Revision=1 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,(await s.Client.PostAsJsonAsync($"/api/calendar-accounts/{s.Account.Id}/calendars",new CreateCalendarRequest("other",null,null))).StatusCode);
    }
}
