using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ProspectionCrm.Api.Dtos.EmailMessages;
using ProspectionCrm.Api.Dtos.Integrations;
using ProspectionCrm.Api.Entities;
using ProspectionCrm.Api.Services.Collection;
using ProspectionCrm.Api.Services.Email;
using Xunit;

namespace ProspectionCrm.Api.Tests;

public sealed class IntegrationSupervisionHistoryTests(AutomationJobDatabase database) : IClassFixture<AutomationJobDatabase>
{
    private sealed class EmailTransport(EmailSendTransportOutcome outcome) : IEmailSendTransport
    {
        public bool IsAvailable => true;
        public int Calls;
        public Task<EmailSendTransportOutcome> DispatchAsync(EmailSendCommand command, CancellationToken token)
        { Calls++; return Task.FromResult(outcome); }
    }
    [Theory]
    [InlineData(EmailSendTransportOutcome.Acknowledged, "waiting")]
    [InlineData(EmailSendTransportOutcome.Unknown, "uncertain")]
    [InlineData(EmailSendTransportOutcome.NotSent, "failed")]
    [InlineData(EmailSendTransportOutcome.Acknowledged, "waiting", 3, false)]
    [InlineData(EmailSendTransportOutcome.Acknowledged, "evidence-pending", 3, true)]
    public async Task EmailDeliveryStatesRemainIndependentOfObservation(EmailSendTransportOutcome outcome, string expected,
        int ageMinutes = 0, bool evidence = false)
    {
        using var host = await SecurityTestHost.CreateAsync(database); var transport = new EmailTransport(outcome); host.EmailSendTransport = transport;
        using var client = host.Client(); await SecurityTestHost.LoginAsync(client);
        await using var db = database.Db();
        db.AutomationRuntimeSettings.Add(new() { WorkspaceId = host.Operator.WorkspaceId, IsEnabled = true, OperatingModeCode = "automatic" });
        await db.SaveChangesAsync();
        var account = (await (await client.PostAsJsonAsync("/api/email-accounts", new CreateEmailAccountRequest(host.Integration.ConnectionId,
            "synthetic", "supervision", "private@example.invalid"))).Content.ReadFromJsonAsync<EmailAccountDto>())!;
        var plan = (await (await client.PostAsJsonAsync("/api/email-send-plans", new EmailSendContent(account.Id,
            ["recipient@example.invalid"], [], "Private subject sentinel", "Private body sentinel"))).Content.ReadFromJsonAsync<EmailSendPlanDto>())!;
        plan = (await (await client.PostAsJsonAsync($"/api/email-send-plans/{plan.Id}/submit", new { revision = plan.Revision })).Content.ReadFromJsonAsync<EmailSendPlanDto>())!;
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync($"/api/automation-action-requests/{plan.AutomationActionRequestId}/approve", new { })).StatusCode);
        EmailSendDelivery delivery;
        if (ageMinutes > 0)
        {
            var started = DateTimeOffset.UtcNow.AddMinutes(-ageMinutes);
            var storedPlan = await db.EmailSendPlans.AsNoTracking().SingleAsync(x => x.Id == plan.Id);
            var op = new IntegrationOperation { Id = Guid.NewGuid(), WorkspaceId = host.Operator.WorkspaceId,
                ConnectionId = host.Integration.ConnectionId, CommandMessageId = Guid.NewGuid(), CorrelationId = Guid.NewGuid(),
                Type = "email.send", Version = 1, StatusCode = "awaiting-result", CreatedAt = started.AddSeconds(-1) };
            delivery = new() { WorkspaceId = op.WorkspaceId, EmailSendPlanId = plan.Id,
                AutomationActionRequestId = plan.AutomationActionRequestId!.Value, EmailAccountId = account.Id,
                ConnectionId = op.ConnectionId.Value, IntegrationOperationId = op.Id, ContentHash = storedPlan.ContentHash,
                ProviderCode = "synthetic", IdempotencyKey = "",
                CreatedAt = op.CreatedAt, StartedAt = started, StatusCode = "awaiting-result" };
            delivery.IdempotencyKey = $"email-send-{delivery.Id:D}";
            db.AddRange(op, delivery, new IntegrationAttempt { Id = Guid.NewGuid(), OperationId = op.Id, Number = 1,
                StatusCode = "acknowledged", StartedAt = started, FinishedAt = started.AddSeconds(1) });
            await db.SaveChangesAsync();
        }
        else
        {
            delivery = (await (await client.PostAsync($"/api/email-send-deliveries/from-plan/{plan.Id}", null)).Content.ReadFromJsonAsync<EmailSendDelivery>())!;
            Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync($"/api/email-send-deliveries/{delivery.Id}/dispatch", null)).StatusCode);
        }
        var expectedCalls = ageMinutes > 0 ? 0 : 1;
        if (evidence)
        {
            db.IntegrationReceipts.Add(new() { MessageId = Guid.NewGuid(), OperationId = delivery.IntegrationOperationId, EvidenceNumber = 1,
                CanonicalContent = "private@example.invalid unvalidated proof", ContentSha256 = new string('a', 64), ReceivedAt = host.Clock.Now });
            await db.SaveChangesAsync();
        }
        var before = await IntegrationSupervisionTests.Fingerprint(db);
        var response = await client.GetStringAsync(IntegrationSupervisionTests.Root + "/operations");
        foreach (var secret in new[] { "private@example.invalid", "recipient@example.invalid", "Private subject sentinel", "Private body sentinel", delivery.ContentHash, delivery.IdempotencyKey })
            Assert.DoesNotContain(secret, response);
        var item = Assert.Single((await client.GetFromJsonAsync<IntegrationSupervisionPage>(IntegrationSupervisionTests.Root + "/operations"))!.Items);
        Assert.Equal(delivery.Id, item.BusinessId); Assert.Equal(expected, item.State); Assert.Equal(expectedCalls, transport.Calls);
        var diagnostic = (await client.GetFromJsonAsync<IntegrationOperationDiagnostic>(IntegrationDiagnosticTests.Route(item.Id)))!;
        Assert.Equal(expected, diagnostic.Operation.State);
        var available = ageMinutes > 0 && !evidence;
        Assert.Equal(available ? "available-at-observation" : expected is "uncertain" or "evidence-pending" ? "requires-validation" : "not-recommended", diagnostic.Reconciliation.StatusCode);
        Assert.Equal(expectedCalls, transport.Calls);
        Assert.Equal(before, await IntegrationSupervisionTests.Fingerprint(db));
        if (available)
        {
            Assert.Contains("HumanReconciliationAvailable", diagnostic.RecommendationCodes);
            Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync($"/api/email-send-deliveries/{delivery.Id}/reconcile", null)).StatusCode);
            Assert.Equal("send-unknown", await db.EmailSendDeliveries.Where(x => x.Id == delivery.Id).Select(x => x.StatusCode).SingleAsync());
            Assert.Equal(expectedCalls, transport.Calls);
        }
    }

    private sealed class CollectionTransport(bool unknown) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { Calls++; if (unknown) throw new HttpRequestException("synthetic uncertainty"); return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Accepted)); }
    }
    [Theory]
    [InlineData(false, "waiting")][InlineData(true, "uncertain")]
    public async Task RemoteCollectionBindingAndUncertainStateAreReadWithoutRecovery(bool unknown, string expected)
    {
        using var host = await SecurityTestHost.CreateAsync(database); var transport = new CollectionTransport(unknown); host.TransportHandler = () => transport;
        using var client = host.Client(); await SecurityTestHost.LoginAsync(client); await using var db = database.Db();
        var pipeline = new Pipeline { WorkspaceId = host.Operator.WorkspaceId, Name = "Supervision", TypeCode = "custom" };
        var stage = new PipelineStage { Pipeline = pipeline, Name = "Inbox", CategoryCode = "active" };
        var source = new SourceConfiguration { WorkspaceId = host.Operator.WorkspaceId, Name = "Private source sentinel", SourceTypeCode = CollectionIntegrationContracts.SourceType };
        var search = new SavedSearch { WorkspaceId = host.Operator.WorkspaceId, Pipeline = pipeline, SourceConfiguration = source,
            Name = "Private query sentinel", CriteriaJson = "{\"query\":\"Private criteria sentinel\",\"maxItems\":10}" };
        db.Add(stage); db.Add(search); await db.SaveChangesAsync();
        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsJsonAsync($"/api/saved-searches/{search.Id}/collection-jobs", new { pipelineStageId = stage.Id })).StatusCode);
        await using (var scope = host.Services.CreateAsyncScope())
            Assert.True(await scope.ServiceProvider.GetRequiredService<ISourceCollectionJobProcessor>().ProcessNextAsync(default));
        var intent = await db.RemoteCollectionIntents.AsNoTracking().SingleAsync(x => x.WorkspaceId == host.Operator.WorkspaceId);
        var before = await IntegrationSupervisionTests.Fingerprint(db);
        var item = Assert.Single((await client.GetFromJsonAsync<IntegrationSupervisionPage>(IntegrationSupervisionTests.Root + "/operations"))!.Items);
        Assert.Equal(intent.JobId, item.BusinessId); Assert.Equal(intent.OperationId, item.Id); Assert.Equal(expected, item.State);
        var raw = await client.GetStringAsync(IntegrationSupervisionTests.Root + "/operations");
        Assert.DoesNotContain("sentinel", raw); Assert.DoesNotContain(intent.Fingerprint, raw);
        var diagnostic = (await client.GetFromJsonAsync<IntegrationOperationDiagnostic>(IntegrationDiagnosticTests.Route(item.Id)))!;
        Assert.Equal(expected, diagnostic.Operation.State);
        Assert.Contains(expected == "uncertain" ? "ManualInvestigationRequired" : "WaitForEvidence", diagnostic.RecommendationCodes);
        Assert.Equal(1, transport.Calls); Assert.Equal(before, await IntegrationSupervisionTests.Fingerprint(db));
    }
}
