using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ProspectionCrm.Api.Dtos.CalendarEvents;
using ProspectionCrm.Api.Entities;
using ProspectionCrm.Api.Services.Calendar;
using Xunit;

namespace ProspectionCrm.Api.Tests;

public sealed class CalendarEventPlanSecurityTests(AutomationJobDatabase database) : CalendarEventPlanFixture(database), IClassFixture<AutomationJobDatabase>
{
    [Fact]
    public async Task HumanCookieCsrfAndStrictDtosAreRequiredAndNoSendRouteExists()
    {
        using var s = await Prepare(); using var anonymous = s.Host.Client();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync("/api/calendar-event-plans", Content(s))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.SendAsync(s.Host.Signed("{}"u8.ToArray(), target: "/api/calendar-event-plans"))).StatusCode);
        s.Client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Client.PostAsJsonAsync("/api/calendar-event-plans", Content(s))).StatusCode);
        await SecurityTestHost.CsrfAsync(s.Client);
        var json = JsonSerializer.Serialize(Content(s), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        foreach (var field in new[] { "participants", "workspaceId", "callbackUrl", "rrule", "seriesId" })
        {
            using var body = new StringContent(json[..^1] + ",\"" + field + "\":\"synthetic\"}", Encoding.UTF8, "application/json");
            Assert.Equal(HttpStatusCode.BadRequest, (await s.Client.PostAsync("/api/calendar-event-plans", body)).StatusCode);
        }
        var p = await Create(s);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Client.PostAsJsonAsync($"/api/calendar-event-plans/{p.Id}/submit", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await s.Client.PostAsJsonAsync($"/api/calendar-event-plans/{p.Id}/send", new { })).StatusCode);
        var pending = await Submit(s, p);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.SendAsync(s.Host.Signed("{}"u8.ToArray(), target: $"/api/automation-action-requests/{pending.AutomationActionRequestId}/approve"))).StatusCode);
        await AssertNoEffects(s);
    }
    [Fact]
    public async Task ForeignWorkspacePlansAccountsAndDecisionsAreInvisible()
    {
        using var a = await Prepare(); using var b = await Prepare(); var p = await Submit(a, await Create(a));
        Assert.Equal(HttpStatusCode.NotFound, (await b.Client.GetAsync($"/api/calendar-event-plans/{p.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Decide(b, p)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.Client.PutAsJsonAsync($"/api/calendar-event-plans/{p.Id}", new UpdateCalendarEventPlanRequest(p.Revision, Content(b)))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.Client.PostAsJsonAsync($"/api/calendar-event-plans/{p.Id}/cancel", new { revision = p.Revision })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.Client.PostAsJsonAsync("/api/calendar-event-plans", Content(b) with { CalendarId = a.Calendar.Id })).StatusCode);
        Assert.Empty((await b.Client.GetFromJsonAsync<CalendarEventPlanDto[]>("/api/calendar-event-plans"))!);
    }
    [Theory]
    [InlineData("contact")][InlineData("company")][InlineData("opportunity")]
    public async Task ExplicitReferencesRequireTheSameWorkspace(string kind)
    {
        using var s = await Prepare(); using var other = await Prepare(); await using var db = Db();
        var company = new Company { WorkspaceId = other.Workspace, Name = "Synthetic" };
        var contact = new Contact { WorkspaceId = other.Workspace, FirstName = "Synthetic", LastName = "Contact", Email = "ignored@example.invalid" };
        var opportunity = new Opportunity { WorkspaceId = other.Workspace, Title = "Synthetic", PriorityCode = "normal",
            PipelineStage = new() { Name = "Open", CategoryCode = "active", Pipeline = new() { WorkspaceId = other.Workspace, Name = "Synthetic", TypeCode = "custom" } } };
        db.AddRange(company, contact, opportunity); await db.SaveChangesAsync();
        var content = kind switch { "contact" => Content(s) with { ContactId = contact.Id }, "company" => Content(s) with { CompanyId = company.Id }, _ => Content(s) with { OpportunityId = opportunity.Id } };
        Assert.Equal(HttpStatusCode.NotFound, (await s.Client.PostAsJsonAsync("/api/calendar-event-plans", content)).StatusCode);
        var own = Content(other) with { ContactId = contact.Id, CompanyId = company.Id, OpportunityId = opportunity.Id };
        var p = await Create(other, own); Assert.Null(p.Content.Target);
        Assert.Equal(contact.Id, p.Content.Content.ContactId); await Submit(other, p);
    }
}
