using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using ProspectionCrm.Api.Dtos.CalendarEvents;
using ProspectionCrm.Api.Dtos.Integrations;
using ProspectionCrm.Api.Services.Calendar;
using ProspectionCrm.Api.Services.Integrations;
using Xunit;

namespace ProspectionCrm.Api.Tests;

public sealed class CalendarSyncConcurrencyTests(AutomationJobDatabase database) : CalendarSyncFixture(database), IClassFixture<AutomationJobDatabase>
{
    [Fact]
    public async Task ConcurrentEnqueuesProduceOneJobAndOperation()
    {
        using var s = await Prepare(dispatch: false, receive: false);
        Assert.Equal(HttpStatusCode.NoContent, (await s.Client.PostAsync($"/api/calendar-sync-jobs/{s.Job.Id}/cancel", null)).StatusCode);
        var replies = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => s.Client.PostAsJsonAsync($"/api/calendars/{s.Calendar.Id}/sync-jobs", new EnqueueCalendarSyncRequest())));
        Assert.Single(replies, x => x.StatusCode == HttpStatusCode.Accepted); Assert.Equal(7, replies.Count(x => x.StatusCode == HttpStatusCode.Conflict));
        await using var db = Db(); Assert.Equal(2, await db.CalendarSyncJobs.CountAsync(x => x.CalendarId == s.Calendar.Id));
        Assert.Equal(2, await db.IntegrationOperations.CountAsync(x => x.WorkspaceId == s.Workspace));
    }

    [Fact]
    public async Task ConcurrentDispatchUsesOneAttemptAndNoDatabaseLocksDuringTransport()
    {
        using var s = await Prepare(dispatch: false, receive: false);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var send = Dispatch(s, async (_, token) => { entered.SetResult(); await release.Task.WaitAsync(token); return IntegrationTransportOutcome.Acknowledged; });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
        try
        {
            var competing = await Assert.ThrowsAsync<CalendarSyncException>(() => Dispatch(s)); Assert.Equal("CalendarDispatchNotReady", competing.Message);
            await using var db = Db(); await using var tx = await db.Database.BeginTransactionAsync();
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                SELECT 1 FROM "CalendarAccounts" WHERE "Id" = {s.Account.Id} FOR UPDATE NOWAIT;
                SELECT 1 FROM "CalendarSyncJobs" WHERE "Id" = {s.Job.Id} FOR UPDATE NOWAIT;
                SELECT 1 FROM "IntegrationOperations" WHERE "Id" = {s.Job.IntegrationOperationId} FOR UPDATE NOWAIT;
                SELECT 1 FROM "CalendarSyncStates" WHERE "CalendarId" = {s.Calendar.Id} FOR UPDATE NOWAIT
                """);
            Assert.Equal(1, await db.IntegrationAttempts.CountAsync(x => x.OperationId == s.Job.IntegrationOperationId)); await tx.CommitAsync();
        }
        finally { release.SetResult(); }
        await send; await AssertState(s, "awaiting-result", 0, null);
    }

    [Fact]
    public async Task ConcurrentReceiversAndProcessorsSerializeAndProduceOnlyOneCommit()
    {
        using var s = await Prepare(receive: false);
        var callbacks = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => s.Client.SendAsync(s.Host.Signed(s.Body))));
        Assert.All(callbacks, x => Assert.Equal(HttpStatusCode.Accepted, x.StatusCode));
        // Independent contexts/connections contend while the account lock is held explicitly.
        await using var lockDb = Db(); await using var tx = await lockDb.Database.BeginTransactionAsync();
        await lockDb.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"CalendarAccounts\" WHERE \"Id\" = {s.Account.Id} FOR UPDATE");
        var processes = Enumerable.Range(0, 8).Select(_ => Process(s)).ToArray();
        Assert.All(processes, x => Assert.False(x.IsCompleted));
        await tx.CommitAsync(); await Task.WhenAll(processes).WaitAsync(TimeSpan.FromSeconds(30));
        await AssertState(s, "succeeded", 2, "cursor-next"); await using var db = Db();
        Assert.Single(await db.IntegrationReceipts.Where(x => x.OperationId == s.Job.IntegrationOperationId).ToListAsync());
        Assert.Equal(1, (await db.CalendarSyncStates.SingleAsync(x => x.CalendarId == s.Calendar.Id)).Revision);
    }

    [Fact]
    public async Task CallbackAndBusinessCommitCanPrecedeTransportAck()
    {
        using var s = await Prepare(dispatch: false, receive: false);
        await Dispatch(s, async (_, _) =>
        {
            Assert.Equal(HttpStatusCode.Accepted, (await s.Client.SendAsync(s.Host.Signed(s.Body))).StatusCode);
            await Process(s); await AssertState(s, "succeeded", 2, "cursor-next"); return IntegrationTransportOutcome.Acknowledged;
        });
        await AssertState(s, "succeeded", 2, "cursor-next"); await using var db = Db();
        Assert.Equal("acknowledged", (await db.IntegrationAttempts.SingleAsync(x => x.OperationId == s.Job.IntegrationOperationId)).StatusCode);
    }

    [Theory]
    [InlineData("unknown")][InlineData("disconnect")][InlineData("failed")][InlineData("crash")]
    public async Task DispatchFailuresNeverBlindlyRedispatchAndLateResultsAreSafe(string outcome)
    {
        using var s = await Prepare(dispatch: false, receive: false);
        Task Send() => Dispatch(s, (_, _) => outcome switch
        {
            "crash" => throw new InvalidOperationException("synthetic process crash"),
            "disconnect" => throw new IOException("synthetic network loss"),
            "failed" => Task.FromResult(IntegrationTransportOutcome.Failed),
            _ => Task.FromResult(IntegrationTransportOutcome.Unknown)
        });
        if (outcome == "crash")
        {
            await Assert.ThrowsAsync<InvalidOperationException>(Send);
            await using var db = Db();
            Assert.Equal("CalendarAttemptNotRecoverable", (await Assert.ThrowsAsync<CalendarSyncException>(() => Sync(s, db).ResolveUnknownAsync(s.Workspace, s.Job.Id, default))).Message);
            await db.IntegrationAttempts.Where(x => x.OperationId == s.Job.IntegrationOperationId).ExecuteUpdateAsync(x => x.SetProperty(p => p.StartedAt, DateTimeOffset.UtcNow.AddMinutes(-3)));
            await Sync(s, db).ResolveUnknownAsync(s.Workspace, s.Job.Id, default);
        }
        else await Send();
        await AssertState(s, outcome == "failed" ? "failed" : "dispatch-unknown", 0, null, outcome == "failed" ? "CalendarDispatchFailed" : "CalendarDispatchUnknown");
        await Assert.ThrowsAsync<CalendarSyncException>(() => Dispatch(s));
        Assert.Equal(HttpStatusCode.Accepted, (await s.Client.SendAsync(s.Host.Signed(s.Body))).StatusCode);
        await Process(s); await AssertState(s, outcome == "failed" ? "failed" : "succeeded", outcome == "failed" ? 0 : 2,
            outcome == "failed" ? null : "cursor-next", outcome == "failed" ? "CalendarDispatchFailed" : null);
    }

    private sealed class CommitGate : DbTransactionInterceptor
    {
        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction, TransactionEventData eventData,
            InterceptionResult result, CancellationToken cancellationToken = default)
        { Reached.SetResult(); await Release.Task.WaitAsync(cancellationToken); return result; }
    }

    [Fact]
    public async Task ProcessWinningCancelRaceCommitsOnceAndCancelCannotUndoMessages()
    {
        using var s = await Prepare(); var gate = new CommitGate(); var process = Process(s, gate);
        await gate.Reached.Task.WaitAsync(TimeSpan.FromSeconds(20));
        var cancel = s.Client.PostAsync($"/api/calendar-sync-jobs/{s.Job.Id}/cancel", null);
        try { Assert.False(cancel.IsCompleted); } finally { gate.Release.SetResult(); }
        await process; Assert.Equal(HttpStatusCode.Conflict, (await cancel).StatusCode); await AssertState(s, "succeeded", 2, "cursor-next");
    }

    [Fact]
    public async Task CancellationAtCommitRollsBackAllChangesAndResultRemainsProcessable()
    {
        using var s = await Prepare(); var gate = new CommitGate(); using var cancel = new CancellationTokenSource();
        await using var db = Db(gate); var process = Sync(s, db).ProcessAsync(s.Workspace, s.Job.Id, cancel.Token);
        await gate.Reached.Task.WaitAsync(TimeSpan.FromSeconds(20)); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => process); await AssertState(s, "result-received", 0, null);
        await Process(s); await AssertState(s, "succeeded", 2, "cursor-next");
    }
}
