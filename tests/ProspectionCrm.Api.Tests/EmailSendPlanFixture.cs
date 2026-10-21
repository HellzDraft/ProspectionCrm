using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ProspectionCrm.Api.Data;
using ProspectionCrm.Api.Dtos.EmailMessages;
using ProspectionCrm.Api.Entities;
using Xunit;

namespace ProspectionCrm.Api.Tests;

public abstract class EmailSendPlanFixture(AutomationJobDatabase database)
{
    protected ProspectionCrmDbContext Db() => database.Db();
    protected sealed class NoNetwork : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { Interlocked.Increment(ref Calls); throw new InvalidOperationException("External transport forbidden in preparation tests."); }
    }
    protected sealed class Scenario(SecurityTestHost host, HttpClient client, EmailAccountDto account, NoNetwork network) : IDisposable
    {
        public SecurityTestHost Host { get; } = host;
        public HttpClient Client { get; } = client;
        public EmailAccountDto Account { get; set; } = account;
        public NoNetwork Network { get; } = network;
        public Guid Workspace => Host.Operator.WorkspaceId;
        public void Dispose() { Client.Dispose(); Host.Dispose(); }
    }
    protected async Task<Scenario> Prepare()
    {
        var host = await SecurityTestHost.CreateAsync(database); var network = new NoNetwork(); host.TransportHandler = () => network;
        var client = host.Client(); await SecurityTestHost.LoginAsync(client);
        await using var db = Db();
        db.AutomationRuntimeSettings.Add(new() { WorkspaceId = host.Operator.WorkspaceId, IsEnabled = true, OperatingModeCode = "automatic" });
        await db.SaveChangesAsync();
        var response = await client.PostAsJsonAsync("/api/email-accounts", new CreateEmailAccountRequest(host.Integration.ConnectionId,
            "synthetic", "preparation-account", "sender@example.invalid"));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return new(host, client, (await response.Content.ReadFromJsonAsync<EmailAccountDto>())!, network);
    }
    protected static EmailSendContent Content(Scenario s) => new(s.Account.Id,
        ["z@example.invalid", "a@example.invalid", "z@example.invalid"], ["c@example.invalid", "a@example.invalid"], "Subject", "Plain text\nSecond line");
    protected static async Task<EmailSendPlanDto> Create(Scenario s, EmailSendContent? content = null)
    {
        var response = await s.Client.PostAsJsonAsync("/api/email-send-plans", content ?? Content(s));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<EmailSendPlanDto>())!;
    }
    protected static async Task<EmailSendPlanDto> Submit(Scenario s, EmailSendPlanDto p)
    {
        var response = await s.Client.PostAsJsonAsync($"/api/email-send-plans/{p.Id}/submit", new { revision = p.Revision });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); return (await response.Content.ReadFromJsonAsync<EmailSendPlanDto>())!;
    }
    protected static Task<HttpResponseMessage> Decide(Scenario s, EmailSendPlanDto p, bool approve = true) =>
        s.Client.PostAsJsonAsync($"/api/automation-action-requests/{p.AutomationActionRequestId}/{(approve ? "approve" : "reject")}", new { decisionNote = "Human decision" });
    protected static async Task<EmailSendPlanDto> Read(Scenario s, Guid id) =>
        (await s.Client.GetFromJsonAsync<EmailSendPlanDto>($"/api/email-send-plans/{id}"))!;
    protected async Task AssertNoEffects(Scenario s)
    {
        await using var db = Db();
        Assert.Empty(await db.EmailMessages.Where(x => x.WorkspaceId == s.Workspace).ToListAsync());
        Assert.Empty(await db.IntegrationOperations.Where(x => x.WorkspaceId == s.Workspace).ToListAsync());
        Assert.Empty(await db.AutomationExecutions.Where(x => x.WorkspaceId == s.Workspace && x.EffectApplied).ToListAsync());
        Assert.Empty(await db.Contacts.Where(x => x.WorkspaceId == s.Workspace).ToListAsync());
        Assert.Empty(await db.Companies.Where(x => x.WorkspaceId == s.Workspace).ToListAsync());
        Assert.Empty(await db.Opportunities.Where(x => x.WorkspaceId == s.Workspace).ToListAsync());
        Assert.Equal(0, s.Network.Calls);
    }
}
