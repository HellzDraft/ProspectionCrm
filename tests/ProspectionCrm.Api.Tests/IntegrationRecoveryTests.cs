using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.EntityFrameworkCore;
using ProspectionCrm.Api.Dtos.Integrations;
using ProspectionCrm.Api.Entities;
using Xunit;

namespace ProspectionCrm.Api.Tests;

public sealed class IntegrationRecoveryTests(AutomationJobDatabase database)
    : CalendarEventDeliveryFixture(database), IClassFixture<AutomationJobDatabase>
{
    private static string RecoverRoute(Guid id) => $"/api/integration-supervision/operations/{id}/recover";
    private static async Task<IntegrationRecoveryRequest> Confirmation(Scenario s, Guid id)
    {
        var diagnostic = (await s.Client.GetFromJsonAsync<IntegrationOperationDiagnostic>(IntegrationDiagnosticTests.Route(id)))!;
        var c = Assert.IsType<IntegrationRecoveryConfirmation>(diagnostic.RecoveryConfirmation);
        return new("process-recorded-evidence", c.Revision, c);
    }
    private async Task<string> BusinessFingerprint()
    {
        await using var db = Db();
        // Audit is the only permitted addition on a rejected, authenticated command.
        return await db.Database.SqlQuery<string>($"""
            SELECT md5(string_agg(v, '' ORDER BY v)) AS "Value" FROM (
              SELECT to_jsonb(t)::text AS v FROM "IntegrationOperations" t
              UNION ALL SELECT to_jsonb(t)::text FROM "IntegrationAttempts" t
              UNION ALL SELECT to_jsonb(t)::text FROM "IntegrationReceipts" t
              UNION ALL SELECT to_jsonb(t)::text FROM "CalendarEventDeliveries" t
              UNION ALL SELECT to_jsonb(t)::text FROM "CalendarEvents" t
            ) rows
            """).SingleAsync();
    }

    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task ValidProofIsProcessedOnceWithAtomicHumanAuditAndNoProviderCall(bool update)
    {
        var provider = new Provider(); using var s = await Prepare(provider);
        var target = update ? await Target(s, provider) : null;
        var d = await Delivery(s, await Approved(s, target is null ? null : UpdateContent(s, target))); await Dispatch(s, d.Id);
        Assert.Equal(HttpStatusCode.Accepted, (await Receive(s, provider.Proof)).StatusCode);
        var request = await Confirmation(s, d.IntegrationOperationId);
        await using var db = Db(); var original = await db.IntegrationReceipts.AsNoTracking().SingleAsync(x => x.OperationId == d.IntegrationOperationId);
        var response = await s.Client.PostAsJsonAsync(RecoverRoute(d.IntegrationOperationId), request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = (await response.Content.ReadFromJsonAsync<IntegrationRecoveryResponse>())!;
        Assert.Equal(d.IntegrationOperationId, result.OperationId); Assert.Equal("recorded-evidence-processed", result.OutcomeCode);
        Assert.Equal("succeeded", (await ReadDelivery(s, d.Id)).StatusCode);
        var proof = await db.IntegrationReceipts.AsNoTracking().SingleAsync(x => x.MessageId == original.MessageId);
        Assert.NotNull(proof.ProcessedAt); Assert.Equal(original.CanonicalContent, proof.CanonicalContent);
        Assert.Equal(original.ContentSha256, proof.ContentSha256); Assert.Equal(original.ReceivedAt, proof.ReceivedAt);
        var audit = await db.ActivityEntries.AsNoTracking().SingleAsync(x => x.EntityId == d.IntegrationOperationId);
        Assert.Equal(s.Host.Operator.UserId, audit.ActorUserId); Assert.Equal("user", audit.ActorTypeCode);
        Assert.Equal("integration.recovery", audit.EventTypeCode); Assert.Contains("processed:succeeded", audit.MetadataJson);
        Assert.Contains("process-recorded-evidence", audit.MetadataJson); Assert.DoesNotContain(provider.Proof.ContentHash, audit.MetadataJson);
        Assert.Null(audit.Summary);
        var beforeReplay = await BusinessFingerprint();
        Assert.Equal(HttpStatusCode.Conflict, (await s.Client.PostAsJsonAsync(RecoverRoute(d.IntegrationOperationId), request)).StatusCode);
        Assert.Equal(beforeReplay, await BusinessFingerprint()); Assert.Equal(1, provider.Calls);
        Assert.Equal(2, await db.ActivityEntries.CountAsync(x => x.EntityId == d.IntegrationOperationId));
        Assert.Null((await s.Client.GetFromJsonAsync<IntegrationOperationDiagnostic>(result.DiagnosticUrl))!.RecoveryConfirmation);
    }

    [Theory]
    [InlineData("stale")][InlineData("business")][InlineData("type")][InlineData("status")]
    [InlineData("receipt")][InlineData("new-proof")][InlineData("legacy-reconciled")]
    public async Task ChangedConfirmationOrStateIsRejectedWithoutBusinessEffect(string change)
    {
        var provider = new Provider(); using var s = await Prepare(provider);
        var d = await Delivery(s, await Approved(s)); await Dispatch(s, d.Id);
        Assert.Equal(HttpStatusCode.Accepted, (await Receive(s, provider.Proof)).StatusCode);
        var request = await Confirmation(s, d.IntegrationOperationId); var c = request.Confirmation;
        request = change switch
        {
            "stale" => request with { ExpectedRevision = c.Revision + 1, Confirmation = c with { Revision = c.Revision + 1 } },
            "business" => request with { Confirmation = c with { BusinessId = Guid.NewGuid() } },
            "type" => request with { Confirmation = c with { Type = "email.send" } },
            "status" => request with { Confirmation = c with { BusinessStatus = "operation-unknown" } },
            "receipt" => request with { Confirmation = c with { ReceiptIds = [Guid.NewGuid()] } },
            _ => request
        };
        if (change == "new-proof") Assert.Equal(HttpStatusCode.Accepted, (await Receive(s, provider.Proof with { MessageId = Guid.NewGuid() })).StatusCode);
        if (change == "legacy-reconciled") await Reconcile(s, d.Id);
        var before = await BusinessFingerprint();
        Assert.Equal(HttpStatusCode.Conflict, (await s.Client.PostAsJsonAsync(RecoverRoute(d.IntegrationOperationId), request)).StatusCode);
        Assert.Equal(before, await BusinessFingerprint()); Assert.Equal(1, provider.Calls);
        await using var db = Db(); Assert.Single(await db.ActivityEntries.Where(x => x.EntityId == d.IntegrationOperationId).ToListAsync());
    }

    [Theory]
    [InlineData("invalid")][InlineData("contradictory")][InlineData("version-conflict")][InlineData("known-conflict")]
    public async Task ConflictsAndInvalidEvidenceRemainUnprocessed(string mode)
    {
        var provider = new Provider(); using var s = await Prepare(provider); var target = await Target(s, provider);
        var d = await Delivery(s, await Approved(s, UpdateContent(s, target))); await Dispatch(s, d.Id);
        var proof = mode == "invalid" ? provider.Proof with { Event = provider.Proof.Event! with { Title = "private sentinel mismatch" } }
            : mode == "version-conflict" ? provider.Proof with { Outcome = "version-conflict", Event = null, ObservedRevision = target.ExternalRevision + 1 }
            : provider.Proof;
        Assert.Equal(HttpStatusCode.Accepted, (await Receive(s, proof)).StatusCode);
        if (mode is "contradictory" or "known-conflict")
            Assert.Equal(HttpStatusCode.Accepted, (await Receive(s, provider.Proof with { MessageId = Guid.NewGuid(), Outcome = "not-applied", Event = null })).StatusCode);
        var request = await Confirmation(s, d.IntegrationOperationId);
        if (mode == "known-conflict") await Reconcile(s, d.Id);
        var before = await BusinessFingerprint(); var response = await s.Client.PostAsJsonAsync(RecoverRoute(d.IntegrationOperationId), request);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode); Assert.DoesNotContain("sentinel", await response.Content.ReadAsStringAsync());
        Assert.Equal(before, await BusinessFingerprint()); Assert.Equal(1, provider.Calls);
        await using var db = Db(); Assert.All(await db.IntegrationReceipts.Where(x => x.OperationId == d.IntegrationOperationId).ToListAsync(), r => Assert.Null(r.ProcessedAt));
    }

    [Fact]
    public async Task TwoConcurrentRecoveriesCommitOneEffectAndOneSuccessAudit()
    {
        var provider = new Provider(); using var s = await Prepare(provider);
        var d = await Delivery(s, await Approved(s)); await Dispatch(s, d.Id);
        Assert.Equal(HttpStatusCode.Accepted, (await Receive(s, provider.Proof)).StatusCode);
        var request = await Confirmation(s, d.IntegrationOperationId);
        var responses = await Task.WhenAll(s.Client.PostAsJsonAsync(RecoverRoute(d.IntegrationOperationId), request),
            s.Client.PostAsJsonAsync(RecoverRoute(d.IntegrationOperationId), request));
        Assert.Single(responses, x => x.StatusCode == HttpStatusCode.OK); Assert.Single(responses, x => x.StatusCode == HttpStatusCode.Conflict);
        await using var db = Db(); Assert.Equal(1, await db.CalendarEvents.CountAsync(x => x.CalendarId == s.Calendar.Id));
        var audits = await db.ActivityEntries.Where(x => x.EntityId == d.IntegrationOperationId).ToListAsync();
        Assert.Equal(2, audits.Count); Assert.Single(audits, x => x.MetadataJson!.Contains("processed:succeeded")); Assert.Equal(1, provider.Calls);
    }

    [Fact]
    public async Task UnavailableBusinessLockIsAControlledRefusalWithoutMutation()
    {
        var provider = new Provider(); using var s = await Prepare(provider); var d = await Delivery(s, await Approved(s));
        await Dispatch(s, d.Id); Assert.Equal(HttpStatusCode.Accepted, (await Receive(s, provider.Proof)).StatusCode);
        var request = await Confirmation(s, d.IntegrationOperationId); var before = await BusinessFingerprint();
        await using var blocker = Db(); await using var tx = await blocker.Database.BeginTransactionAsync();
        _ = await blocker.IntegrationOperations.FromSqlInterpolated($"""
            SELECT * FROM "IntegrationOperations" WHERE "Id"={d.IntegrationOperationId} FOR UPDATE
            """).AsNoTracking().SingleAsync();
        Assert.Equal(HttpStatusCode.Conflict, (await s.Client.PostAsJsonAsync(RecoverRoute(d.IntegrationOperationId), request)).StatusCode);
        await tx.RollbackAsync(); Assert.Equal(before, await BusinessFingerprint()); Assert.Equal(1, provider.Calls);
    }

    [Fact]
    public async Task HumanCookieCsrfAndWorkspaceAreAllRequired()
    {
        var provider = new Provider(); using var s = await Prepare(provider);
        var d = await Delivery(s, await Approved(s)); await Dispatch(s, d.Id);
        Assert.Equal(HttpStatusCode.Accepted, (await Receive(s, provider.Proof)).StatusCode);
        var request = await Confirmation(s, d.IntegrationOperationId); await using var db = Db(); var before = await IntegrationSupervisionTests.Fingerprint(db);
        using var anonymous = s.Host.Client();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync(RecoverRoute(d.IntegrationOperationId), request)).StatusCode);
        using var machine = s.Host.Signed(Encoding.UTF8.GetBytes("{}"), target: RecoverRoute(d.IntegrationOperationId));
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.SendAsync(machine)).StatusCode);
        s.Client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Client.PostAsJsonAsync(RecoverRoute(d.IntegrationOperationId), request)).StatusCode);
        await SecurityTestHost.CsrfAsync(s.Client);
        using var other = await SecurityTestHost.CreateAsync(Database); using var otherClient = other.Client(); await SecurityTestHost.LoginAsync(otherClient);
        // The second operator provisioning is a fixture mutation, outside the protected request interval.
        before = await IntegrationSupervisionTests.Fingerprint(db);
        Assert.Equal(HttpStatusCode.NotFound, (await otherClient.PostAsJsonAsync(RecoverRoute(d.IntegrationOperationId), request)).StatusCode);
        Assert.Equal(before, await IntegrationSupervisionTests.Fingerprint(db)); Assert.Equal(1, provider.Calls);
    }

    [Theory]
    [InlineData("technical.probe")][InlineData("email.send")][InlineData("email.sync")]
    [InlineData("calendar.sync")][InlineData("collection.request")]
    public async Task ExcludedFamiliesAreRefusedAndAudited(string type)
    {
        using var host = await SecurityTestHost.CreateAsync(Database); using var client = host.Client(); await SecurityTestHost.LoginAsync(client);
        await using var db = Db(); var op = new IntegrationOperation { Id = Guid.NewGuid(), WorkspaceId = host.Operator.WorkspaceId,
            ConnectionId = host.Integration.ConnectionId, CommandMessageId = Guid.NewGuid(), CorrelationId = Guid.NewGuid(),
            Version = 1, Type = type, ProbeId = type == "technical.probe" ? Guid.NewGuid() : Guid.Empty, StatusCode = "pending", CreatedAt = host.Clock.Now };
        db.Add(op); await db.SaveChangesAsync(); var before = await BusinessFingerprint();
        var request = new IntegrationRecoveryRequest("process-recorded-evidence", 0,
            new(op.Id, Guid.NewGuid(), type, "pending", 0, [Guid.NewGuid()]));
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(RecoverRoute(op.Id), request)).StatusCode);
        Assert.Equal(before, await BusinessFingerprint());
        Assert.Contains("unsupported-family", (await db.ActivityEntries.SingleAsync(x => x.EntityId == op.Id)).MetadataJson);
    }

    [Fact]
    public async Task NoProofCannotTriggerNoEvidenceRecoveryOrDispatch()
    {
        var provider = new Provider(); using var s = await Prepare(provider); var d = await Delivery(s, await Approved(s));
        var diagnostic = (await s.Client.GetFromJsonAsync<IntegrationOperationDiagnostic>(IntegrationDiagnosticTests.Route(d.IntegrationOperationId)))!;
        Assert.Null(diagnostic.RecoveryConfirmation);
        var request = new IntegrationRecoveryRequest("process-recorded-evidence", 0,
            new(d.IntegrationOperationId, d.Id, d.OperationType, "pending", 0, [Guid.NewGuid()]));
        var before = await BusinessFingerprint();
        Assert.Equal(HttpStatusCode.Conflict, (await s.Client.PostAsJsonAsync(RecoverRoute(d.IntegrationOperationId), request)).StatusCode);
        Assert.Equal(before, await BusinessFingerprint()); Assert.Equal(0, provider.Calls);
    }

    [Fact]
    public async Task AuditWriteFailureRollsBackProofAcknowledgementAndCalendarMutation()
    {
        var provider = new Provider(); using var s = await Prepare(provider); var d = await Delivery(s, await Approved(s));
        await Dispatch(s, d.Id); Assert.Equal(HttpStatusCode.Accepted, (await Receive(s, provider.Proof)).StatusCode);
        var request = await Confirmation(s, d.IntegrationOperationId); await using var db = Db();
        var before = await IntegrationSupervisionTests.Fingerprint(db);
        // Failure injection only in this disposable PostgreSQL test database; no production schema change.
        await db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION recovery_audit_test_failure() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
              IF NEW."EventTypeCode" = 'integration.recovery' THEN RAISE EXCEPTION 'synthetic audit unavailable'; END IF;
              RETURN NEW;
            END $$;
            CREATE TRIGGER recovery_audit_test_failure BEFORE INSERT ON "ActivityEntries"
              FOR EACH ROW EXECUTE FUNCTION recovery_audit_test_failure();
            """);
        try
        {
            var response = await s.Client.PostAsJsonAsync(RecoverRoute(d.IntegrationOperationId), request);
            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
            Assert.DoesNotContain("synthetic audit", await response.Content.ReadAsStringAsync());
            Assert.Equal(before, await IntegrationSupervisionTests.Fingerprint(db)); Assert.Equal(1, provider.Calls);
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync("DROP TRIGGER recovery_audit_test_failure ON \"ActivityEntries\"; DROP FUNCTION recovery_audit_test_failure();");
        }
    }

    [Theory]
    [InlineData("unknown", "operation-unknown")][InlineData("not-applied", "failed")]
    public async Task ValidNonSuccessProofDoesNotForceSucceeded(string outcome, string expected)
    {
        var provider = new Provider(); using var s = await Prepare(provider); var d = await Delivery(s, await Approved(s));
        await Dispatch(s, d.Id); var proof = provider.Proof with { Outcome = outcome, Event = null,
            ProvenAt = outcome == "unknown" ? null : provider.Proof.ProvenAt };
        Assert.Equal(HttpStatusCode.Accepted, (await Receive(s, proof)).StatusCode);
        var request = await Confirmation(s, d.IntegrationOperationId);
        Assert.Equal(HttpStatusCode.OK, (await s.Client.PostAsJsonAsync(RecoverRoute(d.IntegrationOperationId), request)).StatusCode);
        Assert.Equal(expected, (await ReadDelivery(s, d.Id)).StatusCode); Assert.Equal(1, provider.Calls);
        await using var db = Db(); Assert.Equal(0, await db.CalendarEvents.CountAsync(x => x.CalendarId == s.Calendar.Id));
        Assert.Contains("processed:" + expected, (await db.ActivityEntries.SingleAsync(x => x.EntityId == d.IntegrationOperationId)).MetadataJson);
    }

    [Theory]
    [InlineData("action")][InlineData("missing-revision")][InlineData("empty-proofs")][InlineData("duplicate-proofs")][InlineData("extra-field")]
    public async Task InvalidRequestsDoNotMutateOrEchoInput(string mode)
    {
        var provider = new Provider(); using var s = await Prepare(provider);
        var d = await Delivery(s, await Approved(s)); await Dispatch(s, d.Id);
        Assert.Equal(HttpStatusCode.Accepted, (await Receive(s, provider.Proof)).StatusCode);
        var request = await Confirmation(s, d.IntegrationOperationId);
        var json = System.Text.Json.JsonSerializer.SerializeToNode(request, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!.AsObject();
        if (mode == "action") json["action"] = "secret@example.invalid";
        if (mode == "missing-revision") json.Remove("expectedRevision");
        if (mode == "extra-field") json["payload"] = "secret@example.invalid";
        if (mode == "empty-proofs") json["confirmation"]!["receiptIds"] = new System.Text.Json.Nodes.JsonArray();
        if (mode == "duplicate-proofs") json["confirmation"]!["receiptIds"] = new System.Text.Json.Nodes.JsonArray(request.Confirmation.ReceiptIds[0], request.Confirmation.ReceiptIds[0]);
        await using var db = Db(); var before = await IntegrationSupervisionTests.Fingerprint(db);
        var response = await s.Client.PostAsync(RecoverRoute(d.IntegrationOperationId), new StringContent(json.ToJsonString(), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode); Assert.DoesNotContain("secret@example.invalid", await response.Content.ReadAsStringAsync());
        Assert.Equal(before, await IntegrationSupervisionTests.Fingerprint(db));
    }
}
