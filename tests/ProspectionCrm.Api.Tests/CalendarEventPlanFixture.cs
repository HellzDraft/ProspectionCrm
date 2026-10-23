using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ProspectionCrm.Api.Data;
using ProspectionCrm.Api.Dtos.CalendarEvents;
using ProspectionCrm.Api.Entities;
using Xunit;

namespace ProspectionCrm.Api.Tests;

public abstract class CalendarEventPlanFixture(AutomationJobDatabase database)
{
    protected AutomationJobDatabase Database => database;
    protected ProspectionCrmDbContext Db() => database.Db();
    protected sealed class NoNetwork : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { Interlocked.Increment(ref Calls); throw new InvalidOperationException("External transport forbidden in preparation tests."); }
    }
    protected sealed class Scenario(SecurityTestHost host, HttpClient client, CalendarAccountDto account, CalendarDto calendar, NoNetwork network) : IDisposable
    {
        public SecurityTestHost Host { get; } = host;
        public HttpClient Client { get; } = client;
        public CalendarAccountDto Account { get; set; } = account;
        public CalendarDto Calendar { get; } = calendar;
        public NoNetwork Network { get; } = network;
        public Guid Workspace => Host.Operator.WorkspaceId;
        public void Dispose() { Client.Dispose(); Host.Dispose(); }
    }
    protected async Task<Scenario> Prepare(ProspectionCrm.Api.Services.Calendar.ICalendarEventTransport? transport = null, Action<SecurityTestHost>? configure = null)
    {
        var host = await SecurityTestHost.CreateAsync(database); var network = new NoNetwork(); host.TransportHandler = () => network;
        host.CalendarEventTransport = transport;
        configure?.Invoke(host);
        var client = host.Client(); await SecurityTestHost.LoginAsync(client);
        await using var db = Db();
        db.AutomationRuntimeSettings.Add(new() { WorkspaceId = host.Operator.WorkspaceId, IsEnabled = true, OperatingModeCode = "automatic" });
        await db.SaveChangesAsync();
        var response = await client.PostAsJsonAsync("/api/calendar-accounts", new CreateCalendarAccountRequest(host.Integration.ConnectionId,
            "synthetic", "preparation-account", "Synthetic"));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var account = (await response.Content.ReadFromJsonAsync<CalendarAccountDto>())!;
        response = await client.PostAsJsonAsync($"/api/calendar-accounts/{account.Id}/calendars", new CreateCalendarRequest("calendar", "Synthetic", "Europe/Paris"));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return new(host, client, account, (await response.Content.ReadFromJsonAsync<CalendarDto>())!, network);
    }
    protected static CalendarEventContent Content(Scenario s) => new(s.Calendar.Id, "create", null,
        "Proposed", "Description", "Location", "2026-10-21T10:00:00+02:00", "2026-10-21T11:00:00+02:00", false, "Europe/Paris");
    protected static async Task<CalendarEventPlanDto> Create(Scenario s, CalendarEventContent? content = null)
    {
        var response = await s.Client.PostAsJsonAsync("/api/calendar-event-plans", content ?? Content(s));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<CalendarEventPlanDto>())!;
    }
    protected static async Task<CalendarEventPlanDto> Submit(Scenario s, CalendarEventPlanDto p)
    {
        var response = await s.Client.PostAsJsonAsync($"/api/calendar-event-plans/{p.Id}/submit", new { revision = p.Revision });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); return (await response.Content.ReadFromJsonAsync<CalendarEventPlanDto>())!;
    }
    protected static Task<HttpResponseMessage> Decide(Scenario s, CalendarEventPlanDto p, bool approve = true) =>
        s.Client.PostAsJsonAsync($"/api/automation-action-requests/{p.AutomationActionRequestId}/{(approve ? "approve" : "reject")}", new { decisionNote = "Human decision" });
    protected static async Task<CalendarEventPlanDto> Read(Scenario s, Guid id) =>
        (await s.Client.GetFromJsonAsync<CalendarEventPlanDto>($"/api/calendar-event-plans/{id}"))!;
    protected async Task AssertNoEffects(Scenario s)
    {
        await using var db = Db();
        Assert.Empty(await db.CalendarEvents.Where(x => x.WorkspaceId == s.Workspace).ToListAsync());
        Assert.Empty(await db.IntegrationOperations.Where(x => x.WorkspaceId == s.Workspace).ToListAsync());
        Assert.Empty(await db.AutomationExecutions.Where(x => x.WorkspaceId == s.Workspace && x.EffectApplied).ToListAsync());
        Assert.Empty(await db.Contacts.Where(x => x.WorkspaceId == s.Workspace).ToListAsync());
        Assert.Empty(await db.Companies.Where(x => x.WorkspaceId == s.Workspace).ToListAsync());
        Assert.Empty(await db.Opportunities.Where(x => x.WorkspaceId == s.Workspace).ToListAsync());
        Assert.Equal(0, s.Network.Calls);
    }
}
