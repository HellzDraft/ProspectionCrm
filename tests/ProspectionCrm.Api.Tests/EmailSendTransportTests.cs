using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using ProspectionCrm.Api.Dtos.EmailMessages;
using ProspectionCrm.Api.Entities;
using ProspectionCrm.Api.Services.Integrations;
using Xunit;

namespace ProspectionCrm.Api.Tests;

public sealed class EmailSendTransportTests(AutomationJobDatabase database) : IClassFixture<AutomationJobDatabase>
{
    [Theory]
    [InlineData(202, "acknowledged", "awaiting-result")]
    [InlineData(400, "unknown", "send-unknown")]
    [InlineData(401, "unknown", "send-unknown")]
    [InlineData(500, "unknown", "send-unknown")]
    public async Task HttpOutcomeNeverClaimsProviderAcceptanceOrCertainNonSending(int status, string attempt, string state)
    {
        using var host = await SecurityTestHost.CreateAsync(database);
        await using var server = await IntegrationTlsServer.StartAsync(async c =>
        {
            using var stream = new MemoryStream(); await c.Request.Body.CopyToAsync(stream);
            var body = stream.ToArray(); Assert.True(body.Length > 4096);
            Assert.Equal(IntegrationSignature.Sign(Convert.FromBase64String(host.Integration.OutgoingKey.SecretBase64),
                IntegrationSignature.OutgoingAudience, host.Integration.OutgoingKey.Id,
                c.Request.Headers[IntegrationSignature.TimeHeader].ToString(), "POST", "/webhook/email-send", body),
                c.Request.Headers[IntegrationSignature.SignatureHeader].ToString());
            c.Response.StatusCode = status;
        });
        host.Integration.EmailSend = new() { Enabled = true, Endpoint = new UriBuilder(server.Endpoint) { Path = "/webhook/email-send" }.Uri.AbsoluteUri };
        host.Integration.MaxConcurrentSends = 1; // A second gate acquisition would prevent transport.
        host.TransportHandler = () => server.Handler();
        using var client = host.Client(); await SecurityTestHost.LoginAsync(client);
        await using var db = database.Db();
        db.AutomationRuntimeSettings.Add(new() { WorkspaceId = host.Operator.WorkspaceId, IsEnabled = true, OperatingModeCode = "automatic" });
        await db.SaveChangesAsync();
        var account = (await (await client.PostAsJsonAsync("/api/email-accounts", new CreateEmailAccountRequest(host.Integration.ConnectionId,
            "local-demo", "transport-test", "sender@example.invalid"))).Content.ReadFromJsonAsync<EmailAccountDto>())!;
        var plan = (await (await client.PostAsJsonAsync("/api/email-send-plans", new EmailSendContent(account.Id,
            ["recipient@example.invalid"], [], "Transport", new string('a', 5000)))).Content.ReadFromJsonAsync<EmailSendPlanDto>())!;
        plan = (await (await client.PostAsJsonAsync($"/api/email-send-plans/{plan.Id}/submit", new { revision = plan.Revision })).Content.ReadFromJsonAsync<EmailSendPlanDto>())!;
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync($"/api/automation-action-requests/{plan.AutomationActionRequestId}/approve", new { })).StatusCode);
        var delivery = (await (await client.PostAsync($"/api/email-send-deliveries/from-plan/{plan.Id}", null)).Content.ReadFromJsonAsync<EmailSendDelivery>())!;
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync($"/api/email-send-deliveries/{delivery.Id}/dispatch", null)).StatusCode);
        Assert.Equal(state, (await db.EmailSendDeliveries.AsNoTracking().SingleAsync(x => x.Id == delivery.Id)).StatusCode);
        Assert.Equal(attempt, (await db.IntegrationAttempts.SingleAsync(x => x.OperationId == delivery.IntegrationOperationId)).StatusCode);
        Assert.Empty(await db.EmailMessages.Where(x => x.WorkspaceId == host.Operator.WorkspaceId).ToListAsync());
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync($"/api/email-send-deliveries/{delivery.Id}/dispatch", null)).StatusCode);
        Assert.Equal(1, server.Calls);
    }

    [Theory]
    [InlineData("")][InlineData("http://localhost/send")][InlineData("https://user@localhost/send")]
    [InlineData("https://localhost/send?target=x")][InlineData("https://localhost/send#fragment")]
    public async Task UnsafeEndpointRejected(string endpoint)
    {
        using var host = await SecurityTestHost.CreateAsync(database);
        host.Integration.EmailSend = new() { Enabled = true, Endpoint = endpoint };
        Assert.False(host.Integration.IsValid(host.Operator));
    }
    [Fact]
    public async Task SeparateActivationRequiresGlobalActivation()
    {
        using var host = await SecurityTestHost.CreateAsync(database);
        Assert.False(host.Integration.EmailSend.Enabled);
        host.Integration.EmailSend = new() { Enabled = true, Endpoint = "https://localhost/send" };
        Assert.True(host.Integration.IsValid(host.Operator));
        host.Integration.Enabled = false; Assert.False(host.Integration.IsValid(host.Operator));
        host.Integration.EmailSend.Enabled = false; Assert.True(host.Integration.IsValid(host.Operator));
    }
}
