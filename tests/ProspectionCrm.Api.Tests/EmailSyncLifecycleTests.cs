using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using ProspectionCrm.Api.Dtos.EmailMessages;
using ProspectionCrm.Api.Dtos.Integrations;
using ProspectionCrm.Api.Services.Email;
using ProspectionCrm.Api.Services.Integrations;
using Xunit;

namespace ProspectionCrm.Api.Tests;

public sealed class EmailSyncLifecycleTests(AutomationJobDatabase database) : EmailSyncFixture(database), IClassFixture<AutomationJobDatabase>
{
    [Fact]
    public async Task DurableResultImportsAtomicallyAndReplaysAcrossFreshScopesWithoutDuplicates()
    {
        using var s = await Prepare(); await AssertState(s, "result-received", 0, null);
        // The raw receipt is retained only internally; the public pending route returns metadata.
        var pending = await s.Client.GetStringAsync("/api/integrations/pending-receipts");
        Assert.DoesNotContain("canonicalContent", pending); Assert.DoesNotContain("Plain text", pending);
        await Process(s); await AssertState(s, "succeeded", 2, "cursor-next");
        await Process(s); // Lost HTTP response after successful commit.
        Assert.Equal(HttpStatusCode.Accepted, (await s.Client.SendAsync(s.Host.Signed(s.Body))).StatusCode);
        await using var db = Db();
        var messages = await db.EmailMessages.Where(x => x.EmailAccountId == s.Account.Id).ToListAsync();
        Assert.All(messages, x => { Assert.Equal("inbound", x.DirectionCode); Assert.Null(x.ContactId); Assert.Null(x.CompanyId); Assert.Null(x.OpportunityId); Assert.Null(x.Subject); });
        Assert.Null(messages.Single(x => x.ExternalMessageId == "b").BodyText);
        Assert.Empty(await db.Contacts.Where(x => x.WorkspaceId == s.Workspace).ToListAsync());
        Assert.Empty(await db.Companies.Where(x => x.WorkspaceId == s.Workspace).ToListAsync());
        Assert.Empty(await db.Opportunities.Where(x => x.WorkspaceId == s.Workspace).ToListAsync());
        Assert.Equal(1, (await db.EmailSyncStates.SingleAsync(x => x.EmailAccountId == s.Account.Id)).Revision);
        await NewJob(s);
        await Dispatch(s, (command, _) => { Assert.Equal("cursor-next", command.Payload.PreviousCursor); return Task.FromResult(IntegrationTransportOutcome.Acknowledged); });
        var body = await Body(s, [EmailSyncContractTests.Message("b", null), EmailSyncContractTests.Message("a")], cursor: "cursor-2");
        Assert.Equal(HttpStatusCode.Accepted, (await s.Client.SendAsync(s.Host.Signed(body))).StatusCode);
        await Process(s); await AssertState(s, "succeeded", 2, "cursor-2");
        var second = await db.EmailSyncJobs.SingleAsync(x => x.Id == s.Job.Id); Assert.Equal(0, second.CreatedCount); Assert.Equal(2, second.ExistingCount);
        var read = await s.Client.GetFromJsonAsync<EmailMessageDto[]>($"/api/email-messages?emailAccountId={s.Account.Id}&directionCode=inbound");
        Assert.Equal(2, read!.Length); Assert.All(read, x => Assert.Equal(s.Account.Id, x.EmailAccountId));
        Assert.Equal(HttpStatusCode.OK, (await s.Client.GetAsync($"/api/email-messages/{read[0].Id}")).StatusCode);
    }

    [Fact]
    public async Task SameProviderMessageOnAnotherAccountIsDistinctAndDivergentBatchRollsBack()
    {
        using var s = await Prepare(); await Process(s);
        await NewJob(s); await Dispatch(s);
        var body = await Body(s, [EmailSyncContractTests.Message("new-message"), EmailSyncContractTests.Message("a", "Divergent")]);
        Assert.Equal(HttpStatusCode.Accepted, (await s.Client.SendAsync(s.Host.Signed(body))).StatusCode);
        var conflict = await Assert.ThrowsAsync<EmailSyncException>(() => Process(s)); Assert.Equal("EmailMessageContentConflict", conflict.Message);
        await AssertState(s, "failed", 2, "cursor-next", "EmailMessageContentConflict");
        await using var db = Db(); Assert.Equal("a", (await db.EmailSyncJobs.SingleAsync(x => x.Id == s.Job.Id)).ConflictingExternalMessageId);
        Assert.Equal("Plain text", (await db.EmailMessages.SingleAsync(x => x.EmailAccountId == s.Account.Id && x.ExternalMessageId == "a")).BodyText);
        var response = await s.Client.PostAsJsonAsync("/api/email-accounts", new CreateEmailAccountRequest(s.Host.Integration.ConnectionId,
            "synthetic", "account-2", "second@example.invalid"));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode); s.Account = (await response.Content.ReadFromJsonAsync<EmailAccountDto>())!;
        await NewJob(s); await Dispatch(s); body = await Body(s, [EmailSyncContractTests.Message("a")]);
        Assert.Equal(HttpStatusCode.Accepted, (await s.Client.SendAsync(s.Host.Signed(body))).StatusCode);
        await Process(s); await AssertState(s, "succeeded", 1, "cursor-next");
        Assert.Equal(2, await db.EmailMessages.CountAsync(x => x.WorkspaceId == s.Workspace && x.ExternalMessageId == "a"));
    }

    [Theory]
    [InlineData("disabled")][InlineData("revoked")][InlineData("archived")][InlineData("cursor")][InlineData("revision")]
    [InlineData("account-edit")][InlineData("connection")]
    public async Task ChangedContextAfterReceptionFailsWithoutAdvancingCursor(string change)
    {
        using var s = await Prepare(); await using var db = Db();
        if (change is "disabled" or "revoked" or "archived" or "account-edit")
        {
            var update = new UpdateEmailAccountRequest(s.Account.EmailAddress, change == "account-edit" ? "Changed" : null,
                change is "disabled" or "revoked" ? change : "active", change == "archived", s.Account.Revision);
            Assert.Equal(HttpStatusCode.OK, (await s.Client.PutAsJsonAsync($"/api/email-accounts/{s.Account.Id}", update)).StatusCode);
        }
        if (change == "cursor") await db.EmailSyncStates.Where(x => x.EmailAccountId == s.Account.Id).ExecuteUpdateAsync(x => x.SetProperty(p => p.Cursor, "external-change"));
        if (change == "revision") await db.EmailSyncStates.Where(x => x.EmailAccountId == s.Account.Id).ExecuteUpdateAsync(x => x.SetProperty(p => p.Revision, 1));
        if (change == "connection") s.Host.Integration.ConnectionId = Guid.NewGuid();
        var error = await Assert.ThrowsAsync<EmailSyncException>(() => Process(s));
        var code = change switch { "cursor" or "revision" => "EmailCursorConflict", "connection" => "EmailConnectionMismatch", "account-edit" => "EmailAccountChanged", _ => "EmailAccountUnavailable" };
        Assert.Equal(code, error.Message); await AssertState(s, "failed", 0, change == "cursor" ? "external-change" : null, code);
    }

    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task FailedOutcomeAndEmptySuccessHaveExplicitCursorSemantics(bool succeeded)
    {
        using var s = await Prepare(receive: false); var body = await Body(s, [], succeeded ? "succeeded" : "failed", succeeded ? "empty-cursor" : null);
        Assert.Equal(HttpStatusCode.Accepted, (await s.Client.SendAsync(s.Host.Signed(body))).StatusCode);
        await Process(s); await AssertState(s, succeeded ? "succeeded" : "failed", 0, succeeded ? "empty-cursor" : null, succeeded ? null : "EmailProviderFailed");
    }

    [Theory]
    [InlineData("unknown")][InlineData("oversize")][InlineData("duplicate")][InlineData("wrong-account")][InlineData("wrong-command")]
    public async Task InvalidCallbackDoesNotCreateReceiptOrAdvanceState(string change)
    {
        using var s = await Prepare(receive: false); var node = JsonNode.Parse(s.Body)!;
        switch (change)
        {
            case "unknown": node["payload"]!["accessToken"] = "sensitive-sentinel"; break;
            case "oversize": node["payload"]!["messages"]![0]!["bodyText"] = new string('s', 33000); break;
            case "duplicate": node["payload"]!["messages"]!.AsArray().Add(node["payload"]!["messages"]![0]!.DeepClone()); break;
            case "wrong-account": node["payload"]!["accountRef"] = Guid.NewGuid().ToString(); break;
            case "wrong-command": node["commandMessageId"] = Guid.NewGuid().ToString(); break;
        }
        var response = await s.Client.SendAsync(s.Host.Signed(Encoding.UTF8.GetBytes(node.ToJsonString())));
        Assert.Equal(change.StartsWith("wrong-") ? HttpStatusCode.Conflict : HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain("sensitive-sentinel", await response.Content.ReadAsStringAsync());
        await AssertState(s, "awaiting-result", 0, null); await using var db = Db();
        Assert.False(await db.IntegrationReceipts.AnyAsync(x => x.OperationId == s.Job.IntegrationOperationId));
    }

    [Fact]
    public async Task CorruptDurableMessageFailsAtomicallyWithControlledDiagnostic()
    {
        using var s = await Prepare(); await using var db = Db();
        var node = JsonNode.Parse(s.Body)!; node["payload"]!["messages"]![1]!["fromAddress"] = "invalid";
        var json = node.ToJsonString(); var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(json)));
        await db.IntegrationReceipts.Where(x => x.OperationId == s.Job.IntegrationOperationId).ExecuteUpdateAsync(x => x.SetProperty(p => p.CanonicalContent, json).SetProperty(p => p.ContentSha256, hash));
        Assert.Equal("EmailResultInvalid", (await Assert.ThrowsAsync<EmailSyncException>(() => Process(s))).Message);
        await AssertState(s, "failed", 0, null, "EmailResultInvalid");
    }

    private sealed class CommitFault(bool after) : DbTransactionInterceptor
    {
        public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction, TransactionEventData eventData,
            InterceptionResult result, CancellationToken cancellationToken = default) => after ? ValueTask.FromResult(result) : throw new IOException("synthetic pre-commit crash");
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default) =>
            after ? throw new IOException("synthetic lost HTTP response") : Task.CompletedTask;
    }

    [Theory]
    [InlineData("before-process")][InlineData("before-commit")][InlineData("after-commit")]
    public async Task RestartAtEachCommitBoundaryRecoversWithoutDoubleImport(string boundary)
    {
        using var s = await Prepare();
        if (boundary != "before-process") await Assert.ThrowsAsync<IOException>(() => Process(s, new CommitFault(boundary == "after-commit")));
        await AssertState(s, boundary == "after-commit" ? "succeeded" : "result-received", boundary == "after-commit" ? 2 : 0, boundary == "after-commit" ? "cursor-next" : null);
        using var restarted = new SecurityTestHost(Database.Postgres.GetConnectionString(), s.Host.Operator.UserId, s.Workspace);
        restarted.Operator.PasswordHash = s.Host.Operator.PasswordHash; restarted.Integration.ConnectionId = s.Host.Integration.ConnectionId;
        restarted.Integration.OutgoingKey = s.Host.Integration.OutgoingKey; restarted.Integration.IncomingKeys = s.Host.Integration.IncomingKeys;
        using var client = restarted.Client(); await SecurityTestHost.LoginAsync(client);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync($"/api/email-sync-jobs/{s.Job.Id}/process-result", null)).StatusCode);
        await AssertState(s, "succeeded", 2, "cursor-next");
        Assert.Equal(HttpStatusCode.Accepted, (await client.SendAsync(restarted.Signed(s.Body))).StatusCode);
    }

    [Fact]
    public async Task SqlFaultAfterMessagesWereInsertedRollsBackTheEntireTransaction()
    {
        using var s = await Prepare(); await using var db = Db();
        var constraint = "CK_EmailSync_Test_" + Guid.NewGuid().ToString("N");
        // Trusted generated identifier, all business values parameterized.
        var add = $"ALTER TABLE \"EmailSyncStates\" ADD CONSTRAINT \"{constraint}\" CHECK (\"EmailAccountId\" <> '{s.Account.Id}'::uuid OR \"Revision\" = 0)";
        var drop = $"ALTER TABLE \"EmailSyncStates\" DROP CONSTRAINT \"{constraint}\"";
        await db.Database.ExecuteSqlRawAsync(add);
        try { await Assert.ThrowsAsync<PostgresException>(() => Process(s)); await AssertState(s, "result-received", 0, null); }
        finally { await db.Database.ExecuteSqlRawAsync(drop); }
        await Process(s); await AssertState(s, "succeeded", 2, "cursor-next");
    }

    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task CancelBeforeOrAfterReceiptKeepsTraceWithoutImport(bool received)
    {
        using var s = await Prepare(receive: received);
        Assert.Equal(HttpStatusCode.NoContent, (await s.Client.PostAsync($"/api/email-sync-jobs/{s.Job.Id}/cancel", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await s.Client.SendAsync(s.Host.Signed(s.Body))).StatusCode);
        await Process(s); await AssertState(s, "cancelled", 0, null, "EmailSyncCancelled");
        await NewJob(s); await Dispatch(s); // Old result cannot affect this new cursor owner.
        Assert.Equal(HttpStatusCode.Accepted, (await s.Client.SendAsync(s.Host.Signed(s.Body))).StatusCode);
        await AssertState(s, "awaiting-result", 0, null);
    }

    [Fact]
    public async Task DispatchIsDisabledByDefaultAndNoOutboundRouteExists()
    {
        using var s = await Prepare(dispatch: false, receive: false);
        await using var scope = s.Host.Services.CreateAsyncScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<EmailSyncDispatcher>();
        Assert.Equal("EmailSyncTransportDisabled", (await Assert.ThrowsAsync<EmailSyncException>(() => dispatcher.DispatchAsync(s.Workspace, s.Job.Id))).Message);
        await AssertState(s, "queued", 0, null);
        Assert.Equal(HttpStatusCode.NotFound, (await s.Client.PostAsync("/api/emails/send", null)).StatusCode);
    }
}
