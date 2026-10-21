using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ProspectionCrm.Api.Dtos.EmailMessages;
using ProspectionCrm.Api.Entities;
using ProspectionCrm.Api.Services.Email;
using Xunit;

namespace ProspectionCrm.Api.Tests;

public sealed class EmailSendPlanSecurityTests(AutomationJobDatabase database) : EmailSendPlanFixture(database), IClassFixture<AutomationJobDatabase>
{
    [Theory]
    [InlineData("empty")][InlineData("crlf")][InlineData("display")][InlineData("subject")][InlineData("html")]
    [InlineData("recipients")][InlineData("body")][InlineData("control")][InlineData("unicode-bytes")]
    public async Task InvalidContentIsRejectedBeforePersistence(string kind)
    {
        using var s = await Prepare(); var content = Content(s);
        content = kind switch {
            "empty" => content with { ToAddresses = [] }, "crlf" => content with { ToAddresses = ["a@example.invalid\r\nBcc: b@example.invalid"] },
            "display" => content with { ToAddresses = ["Name <a@example.invalid>"] }, "subject" => content with { Subject = "Subject\r\nHeader" },
            "html" => content with { BodyText = "<p>HTML</p>" }, "recipients" => content with { ToAddresses = Enumerable.Repeat("a@example.invalid", 101).ToArray() },
            "body" => content with { BodyText = new string('x', 32001) }, "control" => content with { BodyText = "a\0b" },
            _ => content with { BodyText = new string('界', 32000) }
        };
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Client.PostAsJsonAsync("/api/email-send-plans", content)).StatusCode);
        await using var db = Db(); Assert.Empty(await db.EmailSendPlans.Where(x => x.WorkspaceId == s.Workspace).ToListAsync());
    }
    [Fact]
    public async Task HumanCookieCsrfAndStrictDtosAreRequiredAndNoSendRouteExists()
    {
        using var s = await Prepare(); using var anonymous = s.Host.Client();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync("/api/email-send-plans", Content(s))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.SendAsync(s.Host.Signed("{}"u8.ToArray(), target: "/api/email-send-plans"))).StatusCode);
        s.Client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Client.PostAsJsonAsync("/api/email-send-plans", Content(s))).StatusCode);
        await SecurityTestHost.CsrfAsync(s.Client);
        var json = JsonSerializer.Serialize(Content(s), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        foreach (var field in new[] { "bccAddresses", "workspaceId", "callbackUrl", "accessToken", "fromAddress" })
        {
            using var body = new StringContent(json[..^1] + ",\"" + field + "\":\"synthetic\"}", Encoding.UTF8, "application/json");
            Assert.Equal(HttpStatusCode.BadRequest, (await s.Client.PostAsync("/api/email-send-plans", body)).StatusCode);
        }
        var p = await Create(s);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Client.PostAsJsonAsync($"/api/email-send-plans/{p.Id}/submit", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await s.Client.PostAsJsonAsync($"/api/email-send-plans/{p.Id}/send", new { })).StatusCode);
        var pending = await Submit(s, p);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.SendAsync(s.Host.Signed("{}"u8.ToArray(), target: $"/api/automation-action-requests/{pending.AutomationActionRequestId}/approve"))).StatusCode);
        await AssertNoEffects(s);
    }
    [Fact]
    public async Task ForeignWorkspacePlansAccountsAndDecisionsAreInvisible()
    {
        using var a = await Prepare(); using var b = await Prepare(); var p = await Submit(a, await Create(a));
        Assert.Equal(HttpStatusCode.NotFound, (await b.Client.GetAsync($"/api/email-send-plans/{p.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Decide(b, p)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.Client.PutAsJsonAsync($"/api/email-send-plans/{p.Id}", new UpdateEmailSendPlanRequest(p.Revision, Content(b)))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.Client.PostAsJsonAsync($"/api/email-send-plans/{p.Id}/cancel", new { revision = p.Revision })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.Client.PostAsJsonAsync("/api/email-send-plans", Content(b) with { EmailAccountId = a.Account.Id })).StatusCode);
        Assert.Empty((await b.Client.GetFromJsonAsync<EmailSendPlanDto[]>("/api/email-send-plans"))!);
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
        Assert.Equal(HttpStatusCode.NotFound, (await s.Client.PostAsJsonAsync("/api/email-send-plans", content)).StatusCode);
        var own = Content(other) with { ContactId = contact.Id, CompanyId = company.Id, OpportunityId = opportunity.Id };
        var p = await Create(other, own); Assert.DoesNotContain(contact.Email, p.Content.ToAddresses);
        Assert.Equal(contact.Id, p.Content.ContactId); await Submit(other, p);
    }
    [Fact]
    public void SnapshotParserIsClosedVersionedBoundedAndFingerprintSensitive()
    {
        var snapshot = new EmailSendSnapshot(1, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 4,
            "from@example.invalid", ["to@example.invalid"], [], null, "body", null, null, null);
        var json = EmailSendSnapshots.Serialize(snapshot); Assert.NotNull(EmailSendSnapshots.Parse(json));
        Assert.Null(EmailSendSnapshots.Parse(json[..^1] + ",\"unknown\":true}"));
        Assert.Null(EmailSendSnapshots.Parse(json.Replace("\"version\":1", "\"version\":1,\"version\":1")));
        Assert.Null(EmailSendSnapshots.Parse(json.Replace("\"version\":1", "\"version\":2")));
        Assert.Null(EmailSendSnapshots.Parse(json.Replace("\"subject\":null,", "")));
        Assert.Null(EmailSendSnapshots.Parse("{" + new string(' ', 65536) + "}"));
        foreach (var changed in new[] { snapshot with { BodyText = "other" }, snapshot with { Revision = 5 },
            snapshot with { ToAddresses = ["other@example.invalid"] }, snapshot with { EmailAccountId = Guid.NewGuid() },
            snapshot with { ContactId = Guid.NewGuid() }, snapshot with { FromAddress = "other@example.invalid" } })
            Assert.NotEqual(EmailSendSnapshots.Hash(snapshot), EmailSendSnapshots.Hash(changed));
    }
}
