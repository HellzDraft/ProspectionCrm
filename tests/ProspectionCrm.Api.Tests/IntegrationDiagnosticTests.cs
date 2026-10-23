using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using ProspectionCrm.Api.Dtos.Integrations;
using ProspectionCrm.Api.Entities;
using Xunit;

namespace ProspectionCrm.Api.Tests;

public sealed class IntegrationDiagnosticTests(AutomationJobDatabase database) : IClassFixture<AutomationJobDatabase>
{
    internal static string Route(Guid id) => $"/api/integration-supervision/operations/{id}/diagnostic";
    [Theory]
    [InlineData("pending", null, false, "NormalWaiting", "WaitForEvidence")]
    [InlineData("awaiting-result", "unknown", false, "UncertainWithoutEvidence", "ManualInvestigationRequired")]
    [InlineData("awaiting-result", "failed", false, "OperationFailed", "ManualInvestigationRequired")]
    [InlineData("succeeded", "unknown", false, "OperationCompleted", "NoInterventionRequired")]
    [InlineData("failed", "failed", false, "OperationFailed", "ManualInvestigationRequired")]
    [InlineData("result-received", "acknowledged", true, "EvidencePending", "ExamineExistingEvidence")]
    [InlineData("succeeded", "acknowledged", true, "EvidencePending", "ExamineExistingEvidence")]
    [InlineData("result-received", null, false, "MetadataInconsistent", "ManualInvestigationRequired")]
    public async Task ClassificationAndMetadataNeverMutateOrExposeContents(string status, string? attemptStatus,
        bool receipt, string code, string recommendation)
    {
        using var host = await SecurityTestHost.CreateAsync(database);
        using var client = host.Client(); await SecurityTestHost.LoginAsync(client);
        await using var db = database.Db(); var now = host.Clock.Now;
        var op = new IntegrationOperation { Id = Guid.NewGuid(), WorkspaceId = host.Operator.WorkspaceId,
            ConnectionId = host.Integration.ConnectionId, CommandMessageId = Guid.NewGuid(), CorrelationId = Guid.NewGuid(),
            ProbeId = Guid.NewGuid(), Version = 1, Type = "technical.probe", StatusCode = status,
            CreatedAt = now, CompletedAt = status is "succeeded" or "failed" ? now : null };
        db.Add(op); await db.SaveChangesAsync();
        if (attemptStatus is not null) db.Add(new IntegrationAttempt { Id = Guid.NewGuid(), OperationId = op.Id, Number = 1,
            StatusCode = attemptStatus, StartedAt = now, FinishedAt = now });
        if (receipt) db.Add(new IntegrationReceipt { MessageId = Guid.NewGuid(), OperationId = op.Id,
            CanonicalContent = "private@example.invalid credential payload signature", ContentSha256 = new string('a', 64), ReceivedAt = now });
        await db.SaveChangesAsync(); var before = await IntegrationSupervisionTests.Fingerprint(db);
        var raw = await client.GetStringAsync(Route(op.Id));
        foreach (var sensitive in new[] { "private@example.invalid", "credential", "payload", "signature", "canonicalContent", "contentSha256" })
            Assert.DoesNotContain(sensitive, raw);
        var result = (await client.GetFromJsonAsync<IntegrationOperationDiagnostic>(Route(op.Id)))!;
        Assert.Equal(code, result.DiagnosticCode); Assert.Contains(recommendation, result.RecommendationCodes);
        Assert.Equal(receipt ? 1 : 0, result.Receipts.Total); Assert.Equal(attemptStatus is null ? 0 : 1, result.Attempts.Total);
        Assert.Null(result.Reconciliation.RouteTemplate);
        if (receipt && status == "succeeded") Assert.Contains("EvidencePendingAfterCompletion", result.AnomalyCodes);
        Assert.Equal(before, await IntegrationSupervisionTests.Fingerprint(db));
    }

    [Fact]
    public async Task ForeignAndAbsentIdentifiersAreIdentical404AndRequireHumanSession()
    {
        using var host = await SecurityTestHost.CreateAsync(database); using var other = await SecurityTestHost.CreateAsync(database);
        using var client = host.Client(); await using var db = database.Db();
        var op = new IntegrationOperation { Id = Guid.NewGuid(), WorkspaceId = other.Operator.WorkspaceId, ProbeId = Guid.NewGuid(),
            Version = 1, Type = "technical.probe", StatusCode = "pending", CommandMessageId = Guid.NewGuid(), CorrelationId = Guid.NewGuid(), CreatedAt = host.Clock.Now };
        db.Add(op); await db.SaveChangesAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(Route(op.Id))).StatusCode);
        using var signed = host.Signed([], target: Route(op.Id)); signed.Method = HttpMethod.Get;
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(signed)).StatusCode);
        await SecurityTestHost.LoginAsync(client); var before = await IntegrationSupervisionTests.Fingerprint(db);
        var foreign = await client.GetAsync(Route(op.Id)); var absent = await client.GetAsync(Route(Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode); Assert.Equal(absent.StatusCode, foreign.StatusCode);
        Assert.DoesNotContain(op.Id.ToString(), await foreign.Content.ReadAsStringAsync());
        Assert.Equal(before, await IntegrationSupervisionTests.Fingerprint(db));
    }

    [Theory]
    [InlineData("attemptOffset=-1")][InlineData("receiptOffset=1000001")][InlineData("limit=101")]
    [InlineData("limit=0")][InlineData("limit=private@example.invalid")]
    public async Task InvalidPaginationIsBoundedAndDoesNotEchoInput(string query)
    {
        using var host = await SecurityTestHost.CreateAsync(database); using var client = host.Client(); await SecurityTestHost.LoginAsync(client);
        var response = await client.GetAsync(Route(Guid.NewGuid()) + "?" + query);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain("private@example.invalid", await response.Content.ReadAsStringAsync());
    }
}

public sealed class IntegrationDiagnosticCalendarTests(AutomationJobDatabase database)
    : CalendarEventDeliveryFixture(database), IClassFixture<AutomationJobDatabase>
{
    [Fact]
    public async Task ProcessedUnknownProofDoesNotImplySuccessOrAnAbsenceOfEvidence()
    {
        var provider = new Provider(); using var s = await Prepare(provider);
        var d = await Delivery(s, await Approved(s)); await Dispatch(s, d.Id);
        var proof = provider.Proof with { Outcome = "unknown", Event = null, ProvenAt = null, ObservedRevision = null };
        Assert.Equal(HttpStatusCode.Accepted, (await Receive(s, proof)).StatusCode); await Reconcile(s, d.Id);
        await using var db = Db(); var before = await IntegrationSupervisionTests.Fingerprint(db);
        var result = (await s.Client.GetFromJsonAsync<IntegrationOperationDiagnostic>(IntegrationDiagnosticTests.Route(d.IntegrationOperationId)))!;
        Assert.Equal("UncertainAfterEvidence", result.DiagnosticCode);
        Assert.NotNull(Assert.Single(result.Receipts.Items).ProcessedAt);
        Assert.Contains("WaitForEvidence", result.RecommendationCodes);
        Assert.DoesNotContain("NoInterventionRequired", result.RecommendationCodes);
        Assert.Equal(before, await IntegrationSupervisionTests.Fingerprint(db)); Assert.Equal(1, provider.Calls);
    }

    [Theory]
    [InlineData(false, "EvidenceInconsistent")][InlineData(true, "BusinessConflict")]
    public async Task PersistedProofInvalidityIsDistinctFromBusinessVersionConflict(bool versionConflict, string expected)
    {
        var provider = new Provider(); using var s = await Prepare(provider);
        var target = await Target(s, provider);
        var d = await Delivery(s, await Approved(s, UpdateContent(s, target))); await Dispatch(s, d.Id);
        var proof = versionConflict
            ? provider.Proof with { Outcome = "version-conflict", Event = null, ObservedRevision = target.ExternalRevision + 1 }
            : provider.Proof with { Event = provider.Proof.Event! with { Title = "Different approved content" } };
        Assert.Equal(HttpStatusCode.Accepted, (await Receive(s, proof)).StatusCode); await Reconcile(s, d.Id);
        await using var db = Db(); var before = await IntegrationSupervisionTests.Fingerprint(db); var calls = provider.Calls;
        var result = (await s.Client.GetFromJsonAsync<IntegrationOperationDiagnostic>(IntegrationDiagnosticTests.Route(d.IntegrationOperationId)))!;
        Assert.Equal(expected, result.DiagnosticCode); Assert.Equal("high", result.RiskLevel);
        Assert.Null(result.Reconciliation.RouteTemplate); Assert.Equal(calls, provider.Calls);
        Assert.Equal(before, await IntegrationSupervisionTests.Fingerprint(db));
    }

    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task LateContradictoryEvidenceAndReconciliationAreNeverInferredOrExecuted(bool update)
    {
        var provider = new Provider { Mode = "timeout" }; using var s = await Prepare(provider);
        var target = update ? await Target(s, provider) : null;
        var d = await Delivery(s, await Approved(s, target is null ? null : UpdateContent(s, target)));
        async Task<IntegrationOperationDiagnostic> Read()
        {
            await using var db = Db(); var before = await IntegrationSupervisionTests.Fingerprint(db); var calls = provider.Calls;
            var result = (await s.Client.GetFromJsonAsync<IntegrationOperationDiagnostic>(IntegrationDiagnosticTests.Route(d.IntegrationOperationId)))!;
            Assert.Equal(before, await IntegrationSupervisionTests.Fingerprint(db)); Assert.Equal(calls, provider.Calls); return result;
        }
        Assert.Equal("NormalWaiting", (await Read()).DiagnosticCode);
        await Dispatch(s, d.Id); Assert.Equal("UncertainWithoutEvidence", (await Read()).DiagnosticCode);
        Assert.Equal(HttpStatusCode.Accepted, (await Receive(s, provider.Proof)).StatusCode);
        var pending = await Read(); Assert.Equal("EvidencePending", pending.DiagnosticCode);
        Assert.Equal("requires-validation", pending.Reconciliation.StatusCode);
        Assert.Equal("/api/calendar-event-deliveries/{id}/reconcile", pending.Reconciliation.RouteTemplate);
        Assert.Equal("not-assessed", pending.EvidenceAssessment);
        await Reconcile(s, d.Id); Assert.Equal("OperationCompleted", (await Read()).DiagnosticCode);
        Assert.Equal(HttpStatusCode.Accepted, (await Receive(s, provider.Proof with { MessageId = Guid.NewGuid(), Outcome = "not-applied", Event = null })).StatusCode);
        Assert.Equal("EvidencePending", (await Read()).DiagnosticCode); // No payload parsing or reconciliation in the GET.
        await Reconcile(s, d.Id);
        var conflict = await Read(); Assert.Equal("EvidenceContradictory", conflict.DiagnosticCode);
        Assert.Equal("high", conflict.RiskLevel); Assert.Null(conflict.Reconciliation.RouteTemplate);
        Assert.Contains("ManualInvestigationRequired", conflict.RecommendationCodes);
        var first = (await s.Client.GetFromJsonAsync<IntegrationOperationDiagnostic>(IntegrationDiagnosticTests.Route(d.IntegrationOperationId) + "?limit=1"))!;
        var second = (await s.Client.GetFromJsonAsync<IntegrationOperationDiagnostic>(IntegrationDiagnosticTests.Route(d.IntegrationOperationId) + "?limit=1&receiptOffset=1&attemptOffset=1"))!;
        Assert.Equal(2, first.Receipts.Total); Assert.Single(first.Receipts.Items); Assert.Single(second.Receipts.Items);
        Assert.NotEqual(first.Receipts.Items[0].MessageId, second.Receipts.Items[0].MessageId);
        Assert.NotNull(first.Receipts.Items[0].ProcessedAt); Assert.Null(second.Receipts.Items[0].ProcessedAt); Assert.Empty(second.Attempts.Items);
        Assert.Equal("EvidenceContradictory", second.DiagnosticCode); // Classification independent of page.
    }
}
