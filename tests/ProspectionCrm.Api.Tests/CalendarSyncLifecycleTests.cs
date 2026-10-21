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
using ProspectionCrm.Api.Dtos.CalendarEvents;
using ProspectionCrm.Api.Dtos.Integrations;
using ProspectionCrm.Api.Services.Calendar;
using ProspectionCrm.Api.Services.Integrations;
using Xunit;

namespace ProspectionCrm.Api.Tests;

public sealed class CalendarSyncLifecycleTests(AutomationJobDatabase database) : CalendarSyncFixture(database), IClassFixture<AutomationJobDatabase>
{
    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task FailedOutcomeAndEmptySuccessHaveExplicitCursorSemantics(bool succeeded)
    {
        using var s = await Prepare(receive: false); var body = await Body(s, [], succeeded ? "succeeded" : "failed", succeeded ? "empty-cursor" : null);
        Assert.Equal(HttpStatusCode.Accepted, (await s.Client.SendAsync(s.Host.Signed(body))).StatusCode);
        await Process(s); await AssertState(s, succeeded ? "succeeded" : "failed", 0, succeeded ? "empty-cursor" : null, succeeded ? null : "CalendarProviderFailed");
    }

    [Theory]
    [InlineData("unknown")][InlineData("oversize")][InlineData("duplicate")][InlineData("wrong-account")][InlineData("wrong-command")]
    public async Task InvalidCallbackDoesNotCreateReceiptOrAdvanceState(string change)
    {
        using var s = await Prepare(receive: false); var node = JsonNode.Parse(s.Body)!;
        switch (change)
        {
            case "unknown": node["payload"]!["accessToken"] = "sensitive-sentinel"; break;
            case "oversize": node["payload"]!["events"]![0]!["description"] = new string('s', 10001); break;
            case "duplicate": node["payload"]!["events"]!.AsArray().Add(node["payload"]!["events"]![0]!.DeepClone()); break;
            case "wrong-account": node["payload"]!["calendarRef"] = Guid.NewGuid().ToString(); break;
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
        var node = JsonNode.Parse(s.Body)!; node["payload"]!["events"]![1]!["status"] = "invalid";
        var json = node.ToJsonString(); var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(json)));
        await db.IntegrationReceipts.Where(x => x.OperationId == s.Job.IntegrationOperationId).ExecuteUpdateAsync(x => x.SetProperty(p => p.CanonicalContent, json).SetProperty(p => p.ContentSha256, hash));
        Assert.Equal("CalendarResultInvalid", (await Assert.ThrowsAsync<CalendarSyncException>(() => Process(s))).Message);
        await AssertState(s, "failed", 0, null, "CalendarResultInvalid");
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
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync($"/api/calendar-sync-jobs/{s.Job.Id}/process-result", null)).StatusCode);
        await AssertState(s, "succeeded", 2, "cursor-next");
        Assert.Equal(HttpStatusCode.Accepted, (await client.SendAsync(restarted.Signed(s.Body))).StatusCode);
    }

    [Fact]
    public async Task SqlFaultAfterMessagesWereInsertedRollsBackTheEntireTransaction()
    {
        using var s = await Prepare(); await using var db = Db();
        var constraint = "CK_CalendarSync_Test_" + Guid.NewGuid().ToString("N");
        // Trusted generated identifier, all business values parameterized.
        var add = $"ALTER TABLE \"CalendarSyncStates\" ADD CONSTRAINT \"{constraint}\" CHECK (\"CalendarId\" <> '{s.Calendar.Id}'::uuid OR \"Revision\" = 0)";
        var drop = $"ALTER TABLE \"CalendarSyncStates\" DROP CONSTRAINT \"{constraint}\"";
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
        Assert.Equal(HttpStatusCode.NoContent, (await s.Client.PostAsync($"/api/calendar-sync-jobs/{s.Job.Id}/cancel", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await s.Client.SendAsync(s.Host.Signed(s.Body))).StatusCode);
        await Process(s); await AssertState(s, "cancelled", 0, null, "CalendarSyncCancelled");
        await NewJob(s); await Dispatch(s); // Old result cannot affect this new cursor owner.
        Assert.Equal(HttpStatusCode.Accepted, (await s.Client.SendAsync(s.Host.Signed(s.Body))).StatusCode);
        await AssertState(s, "awaiting-result", 0, null);
    }

    [Fact]
    public async Task DispatchIsDisabledByDefaultAndNoOutboundRouteExists()
    {
        using var s = await Prepare(dispatch: false, receive: false);
        await using var scope = s.Host.Services.CreateAsyncScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<CalendarSyncDispatcher>();
        Assert.Equal("CalendarSyncTransportDisabled", (await Assert.ThrowsAsync<CalendarSyncException>(() => dispatcher.DispatchAsync(s.Workspace, s.Job.Id))).Message);
        await AssertState(s, "queued", 0, null);
        Assert.Equal(HttpStatusCode.NotFound, (await s.Client.PostAsync("/api/emails/send", null)).StatusCode);
    }
}
