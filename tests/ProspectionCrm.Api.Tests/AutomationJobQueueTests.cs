using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using ProspectionCrm.Api.Entities;
using ProspectionCrm.Api.Services.Automation;
using Xunit;

namespace ProspectionCrm.Api.Tests;

public sealed class AutomationJobQueueTests(AutomationJobDatabase database) : AutomationJobFixture(database), IClassFixture<AutomationJobDatabase>
{
    [Fact]
    public async Task EnqueueDefaultsAndFutureUtcContextAreDurableEvenWhenDisabled()
    {
        var workspace = await Workspace(); var rule = await Rule(workspace);
        var immediate = await Add(workspace);
        var future = DateTimeOffset.UtcNow.AddDays(1).ToOffset(TimeSpan.FromHours(2));
        var delayed = await Add(workspace, Request(rule: rule, available: future, context: "{\"event\":42}"));
        await using var db = Db();
        Assert.False((await db.AutomationRuntimeSettings.SingleAsync(x => x.WorkspaceId == workspace)).IsEnabled);
        Assert.Equal(2, await db.AutomationJobs.CountAsync(x => x.WorkspaceId == workspace));
        Assert.Equal(AutomationJobStatuses.Pending, immediate.StatusCode); Assert.Equal(50, immediate.Priority);
        Assert.Equal(immediate.CreatedAt, immediate.AvailableAt); Assert.Equal(TimeSpan.Zero, immediate.CreatedAt.Offset);
        Assert.Null(immediate.UpdatedAt); Assert.Null(immediate.CompletedAt); Assert.Null(immediate.LeaseOwner);
        Assert.Null(immediate.LeaseExpiresAt); Assert.Null(immediate.LastError); Assert.Null(immediate.ContextJson); Assert.Equal(0, immediate.AttemptCount);
        Assert.Equal(future.ToUnixTimeMilliseconds(), delayed.AvailableAt.ToUnixTimeMilliseconds()); Assert.Equal(TimeSpan.Zero, delayed.AvailableAt.Offset);
        Assert.Equal(rule, delayed.AutomationRuleId);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("{\"event\":42}"), JsonNode.Parse(delayed.ContextJson!)));
        Assert.Empty(await db.AutomationExecutions.Where(x => x.WorkspaceId == workspace).ToListAsync());
        Assert.Empty(await db.EmailMessages.Where(x => x.WorkspaceId == workspace).ToListAsync());
        Assert.Empty(await db.Applications.ToListAsync());
        Assert.Empty(await db.Proposals.ToListAsync());
    }

    [Fact]
    public async Task NullKeysAreIndependentAndNonNullKeysRemainIdempotentAfterTermination()
    {
        var workspace = await Workspace(); var other = await Workspace();
        Assert.NotEqual((await Add(workspace)).Id, (await Add(workspace)).Id);
        var job = await Add(workspace, Request("event"));
        await using var db = Db(); var queue = Queue(db);
        Assert.True(await queue.CancelAsync(workspace, job.Id, default));
        var duplicate = await queue.EnqueueAsync(workspace, Request("event", priority: 100, category: "email"), default);
        Assert.Equal(200, duplicate.Status); Assert.Equal(job.Id, duplicate.Value!.Id);
        Assert.Equal(50, duplicate.Value.Priority); Assert.Equal("general", duplicate.Value.ActionCategoryCode);
        Assert.Equal(AutomationJobStatuses.Cancelled, duplicate.Value.StatusCode);
        Assert.NotEqual(job.Id, (await Add(other, Request("event"))).Id);
        Assert.NotEqual(job.Id, (await Add(workspace, Request("Event"))).Id);
    }

    [Fact]
    public async Task ConcurrentEnqueueReturnsOneCreatedJobAndTheSameDurableWinner()
    {
        var workspace = await Workspace(); var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); int arrivals = 0;
        async Task<AutomationJobResult<AutomationJob>> Enqueue()
        {
            await using var db = Db(); if (Interlocked.Increment(ref arrivals) == 8) gate.SetResult();
            await gate.Task; return await Queue(db).EnqueueAsync(workspace, Request("same-event"), default);
        }
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Enqueue())).WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Single(results, x => x.Status == 201); Assert.Equal(7, results.Count(x => x.Status == 200));
        Assert.Single(results.Select(x => x.Value!.Id).Distinct());
        await using var verify = Db(); Assert.Equal(1, await verify.AutomationJobs.CountAsync(x => x.WorkspaceId == workspace));
    }

    [Theory]
    [InlineData("trigger")]
    [InlineData("category")]
    [InlineData("priority-low")]
    [InlineData("priority-high")]
    [InlineData("json")]
    [InlineData("json-array")]
    [InlineData("json-empty")]
    [InlineData("json-large")]
    [InlineData("json-null-character")]
    [InlineData("key-empty")]
    [InlineData("key-large")]
    [InlineData("key-control")]
    public async Task QueueValidatesInternalCallersToo(string invalid)
    {
        var workspace = await Workspace(); await using var db = Db();
        var request = invalid switch
        {
            "trigger" => Request(trigger: "unknown"), "category" => Request(category: "unknown"),
            "priority-low" => Request(priority: -1), "priority-high" => Request(priority: 101),
            "json" => Request(context: "{bad}"), "json-array" => Request(context: "[]"), "json-empty" => Request(context: ""),
            "json-large" => Request(context: "{\"data\":\"" + new string('é', AutomationJobLimits.ContextBytes) + "\"}"),
            "json-null-character" => Request(context: "{\"data\":\"\\u0000\"}"),
            "key-empty" => Request(" "), "key-large" => Request(new string('x', 201)), _ => Request("a\0b")
        };
        Assert.Equal(400, (await Queue(db).EnqueueAsync(workspace, request, default)).Status);
        Assert.Empty(await db.AutomationJobs.Where(x => x.WorkspaceId == workspace).ToListAsync());
    }

    [Fact]
    public async Task ClaimOrderingUsesPriorityThenAvailabilityThenCreationThenIdAndSkipsFutureAndOtherWorkspace()
    {
        var workspace = await Workspace(); var other = await Workspace(); var now = DateTimeOffset.UtcNow.AddHours(-2);
        var low = await Add(workspace, Request(priority: 0, available: now));
        var later = await Add(workspace, Request(priority: 100, available: now.AddHours(1)));
        var earlier = await Add(workspace, Request(priority: 100, available: now));
        var newer = await Add(workspace, Request(priority: 100, available: now));
        var tie = await Add(workspace, Request(priority: 100, available: now));
        await Add(workspace, Request(priority: 100, available: DateTimeOffset.UtcNow.AddDays(1)));
        await Add(other, Request(priority: 100, available: now));
        await using var db = Db();
        await db.AutomationJobs.Where(x => x.Id == earlier.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.CreatedAt, now));
        await db.AutomationJobs.Where(x => x.Id == newer.Id || x.Id == tie.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.CreatedAt, now.AddMinutes(1)));
        var tied = await db.AutomationJobs.Where(x => x.Id == newer.Id || x.Id == tie.Id).OrderBy(x => x.Id).Select(x => x.Id).ToListAsync();
        foreach (var expected in new[] { earlier.Id, tied[0], tied[1], later.Id, low.Id })
            Assert.Equal(expected, (await Queue(db).ClaimAsync(workspace, default))!.Id);
        Assert.Null(await Queue(db).ClaimAsync(workspace, default));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ConcurrentConsumersNeverShareAJob(int jobs)
    {
        var workspace = await Workspace(); for (int i = 0; i < jobs; i++) await Add(workspace);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); int arrivals = 0;
        async Task<AutomationJob?> Claim()
        {
            await using var db = Db(); if (Interlocked.Increment(ref arrivals) == 2) gate.SetResult();
            await gate.Task; return await Queue(db).ClaimAsync(workspace, default);
        }
        var claimed = (await Task.WhenAll(Claim(), Claim()).WaitAsync(TimeSpan.FromSeconds(20))).OfType<AutomationJob>().ToArray();
        Assert.Equal(jobs, claimed.Length); Assert.Equal(jobs, claimed.Select(x => x.Id).Distinct().Count());
        Assert.Equal(jobs, claimed.Select(x => x.LeaseOwner).Distinct().Count());
        Assert.All(claimed, x => { Assert.Equal(1, x.AttemptCount); Assert.Equal(AutomationJobStatuses.Leased, x.StatusCode);
            Assert.NotNull(x.LeaseOwner); Assert.Equal(TimeSpan.FromMinutes(5), x.LeaseExpiresAt - x.UpdatedAt); });
    }

    [Fact]
    public async Task LockedFirstCandidateIsSkippedWithoutWaiting()
    {
        var workspace = await Workspace(); var first = await Add(workspace, Request(priority: 100)); var second = await Add(workspace);
        await using var locked = Db(); await using var transaction = await locked.Database.BeginTransactionAsync();
        await locked.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"AutomationJobs\" WHERE \"Id\" = {first.Id} FOR UPDATE");
        await using var db = Db();
        Assert.Equal(second.Id, (await Queue(db).ClaimAsync(workspace, default).WaitAsync(TimeSpan.FromSeconds(5)))!.Id);
        await transaction.CommitAsync(); Assert.Equal(first.Id, (await Queue(db).ClaimAsync(workspace, default))!.Id);
    }

    [Fact]
    public async Task RecoveryPreservesContextAndAttemptsAndFencesPreviousOwner()
    {
        var workspace = await Workspace(); await Add(workspace, Request(context: "{\"event\":1}")); await Add(workspace);
        await using var db = Db(); var queue = Queue(db);
        var expired = (await queue.ClaimAsync(workspace, default))!; var live = (await queue.ClaimAsync(workspace, default))!;
        await db.AutomationJobs.Where(x => x.Id == expired.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.LeaseExpiresAt, DateTimeOffset.UtcNow.AddSeconds(-1)));
        Assert.False(await queue.CompleteAsync(workspace, expired.Id, expired.LeaseOwner!, default));
        Assert.False(await queue.FailAsync(workspace, expired.Id, expired.LeaseOwner!, "secret", default));
        Assert.False(await queue.ReleaseAsync(workspace, expired.Id, expired.LeaseOwner!, null, default));
        Assert.Equal(0, await queue.RecoverAsync(Guid.NewGuid(), default));
        Assert.Equal(1, await queue.RecoverAsync(workspace, default)); Assert.Equal(0, await queue.RecoverAsync(workspace, default));
        var recovered = await db.AutomationJobs.AsNoTracking().SingleAsync(x => x.Id == expired.Id);
        Assert.Equal(AutomationJobStatuses.Pending, recovered.StatusCode); Assert.Null(recovered.LeaseOwner); Assert.Null(recovered.LeaseExpiresAt);
        Assert.Equal(expired.ContextJson, recovered.ContextJson); Assert.Equal(1, recovered.AttemptCount); Assert.True(recovered.UpdatedAt >= expired.UpdatedAt);
        Assert.Equal(live.LeaseOwner, (await db.AutomationJobs.AsNoTracking().SingleAsync(x => x.Id == live.Id)).LeaseOwner);
        var reclaimed = (await queue.ClaimAsync(workspace, default))!; Assert.Equal(2, reclaimed.AttemptCount); Assert.NotEqual(expired.LeaseOwner, reclaimed.LeaseOwner);
        Assert.False(await queue.CompleteAsync(workspace, reclaimed.Id, expired.LeaseOwner!, default));
        Assert.True(await queue.CompleteAsync(workspace, reclaimed.Id, reclaimed.LeaseOwner!, default));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TerminalTransitionsRequireCurrentOwnerWorkspaceAndLease(bool fail)
    {
        var workspace = await Workspace(); var original = await Add(workspace); await using var db = Db(); var queue = Queue(db);
        async Task<bool> Finish(Guid w, string owner) => fail
            ? await queue.FailAsync(w, original.Id, owner, "password=secret; SELECT " + new string('x', 5000), default)
            : await queue.CompleteAsync(w, original.Id, owner, default);
        Assert.False(await Finish(workspace, "unowned"));
        var job = (await queue.ClaimAsync(workspace, default))!;
        Assert.False(await Finish(Guid.NewGuid(), job.LeaseOwner!)); Assert.False(await Finish(workspace, "wrong"));
        Assert.False(await queue.CancelAsync(workspace, job.Id, default));
        Assert.True(await Finish(workspace, job.LeaseOwner!)); Assert.False(await Finish(workspace, job.LeaseOwner!));
        Assert.False(await queue.CancelAsync(workspace, job.Id, default));
        Assert.False(await queue.ReleaseAsync(workspace, job.Id, job.LeaseOwner!, null, default));
        var stored = await db.AutomationJobs.AsNoTracking().SingleAsync(x => x.Id == job.Id);
        Assert.Equal(fail ? AutomationJobStatuses.Failed : AutomationJobStatuses.Completed, stored.StatusCode);
        Assert.Equal(fail ? AutomationJobErrors.JobFailed : null, stored.LastError);
        Assert.NotNull(stored.CompletedAt); Assert.Null(stored.LeaseOwner); Assert.Null(stored.LeaseExpiresAt);
        Assert.Null(await queue.ClaimAsync(workspace, default)); Assert.Equal(0, await queue.RecoverAsync(workspace, default));
    }

    [Fact]
    public async Task ReleaseAndCancellationHaveExplicitIdempotentContracts()
    {
        var workspace = await Workspace(); await Add(workspace); await using var db = Db(); var queue = Queue(db);
        var job = (await queue.ClaimAsync(workspace, default))!;
        Assert.False(await queue.ReleaseAsync(workspace, job.Id, "wrong", null, default));
        Assert.True(await queue.ReleaseAsync(workspace, job.Id, job.LeaseOwner!, DateTimeOffset.UtcNow.AddDays(1), default));
        Assert.False(await queue.ReleaseAsync(workspace, job.Id, job.LeaseOwner!, null, default));
        Assert.Null(await queue.ClaimAsync(workspace, default));
        var stored = await db.AutomationJobs.AsNoTracking().SingleAsync(x => x.Id == job.Id);
        Assert.Equal(1, stored.AttemptCount); Assert.Null(stored.LeaseOwner); Assert.Null(stored.LeaseExpiresAt); Assert.Null(stored.CompletedAt);
        Assert.False(await queue.CancelAsync(Guid.NewGuid(), job.Id, default));
        Assert.True(await queue.CancelAsync(workspace, job.Id, default));
        var cancelled = await db.AutomationJobs.AsNoTracking().SingleAsync(x => x.Id == job.Id);
        Assert.True(await queue.CancelAsync(workspace, job.Id, default));
        Assert.Equal(cancelled.CompletedAt, (await db.AutomationJobs.AsNoTracking().SingleAsync(x => x.Id == job.Id)).CompletedAt);
        Assert.False(await queue.CancelAsync(workspace, Guid.NewGuid(), default));
        Assert.False(await queue.CompleteAsync(workspace, job.Id, job.LeaseOwner!, default));
    }

    [Fact]
    public async Task ConcurrentCancellationAndClaimHaveOnlyOneWinner()
    {
        var workspace = await Workspace(); var job = await Add(workspace);
        await using var a = Db(); await using var b = Db();
        var cancellation = Queue(a).CancelAsync(workspace, job.Id, default);
        var claim = Queue(b).ClaimAsync(workspace, default);
        await Task.WhenAll(cancellation, claim).WaitAsync(TimeSpan.FromSeconds(10));
        var cancelled = await cancellation;
        Assert.NotEqual(cancelled, await claim is not null);
        var stored = await a.AutomationJobs.AsNoTracking().SingleAsync(x => x.Id == job.Id);
        Assert.Equal(cancelled ? AutomationJobStatuses.Cancelled : AutomationJobStatuses.Leased, stored.StatusCode);
    }

    [Fact]
    public async Task ConcurrentFinalizersCannotOverwriteEachOthersTerminalState()
    {
        var workspace = await Workspace(); await Add(workspace);
        await using var a = Db(); await using var b = Db(); var job = (await Queue(a).ClaimAsync(workspace, default))!;
        var complete = Queue(a).CompleteAsync(workspace, job.Id, job.LeaseOwner!, default);
        var fail = Queue(b).FailAsync(workspace, job.Id, job.LeaseOwner!, AutomationJobErrors.ProcessingRejected, default);
        await Task.WhenAll(complete, fail).WaitAsync(TimeSpan.FromSeconds(10));
        var completed = await complete; Assert.NotEqual(completed, await fail);
        var stored = await a.AutomationJobs.AsNoTracking().SingleAsync(x => x.Id == job.Id);
        Assert.Equal(completed ? AutomationJobStatuses.Completed : AutomationJobStatuses.Failed, stored.StatusCode);
        Assert.Equal(completed ? null : AutomationJobErrors.ProcessingRejected, stored.LastError);
    }

    [Fact]
    public async Task ImmediateReleaseCanBeReclaimedWithNewCapabilityAndConfiguredDuration()
    {
        var workspace = await Workspace(); await Add(workspace); await using var db = Db();
        var queue = Queue(db, TimeSpan.FromMinutes(2)); var first = (await queue.ClaimAsync(workspace, default))!;
        Assert.Equal(TimeSpan.FromMinutes(2), first.LeaseExpiresAt - first.UpdatedAt);
        Assert.True(await queue.ReleaseAsync(workspace, first.Id, first.LeaseOwner!, null, default));
        var second = (await queue.ClaimAsync(workspace, default))!;
        Assert.Equal(first.Id, second.Id); Assert.Equal(2, second.AttemptCount); Assert.NotEqual(first.LeaseOwner, second.LeaseOwner);
        Assert.False(await queue.FailAsync(workspace, second.Id, first.LeaseOwner!, null, default));
        Assert.True(await queue.FailAsync(workspace, second.Id, second.LeaseOwner!, AutomationJobErrors.ProcessingRejected, default));
        Assert.Equal(AutomationJobErrors.ProcessingRejected, (await db.AutomationJobs.AsNoTracking().SingleAsync(x => x.Id == first.Id)).LastError);
    }

    [Fact]
    public async Task CompletionRechecksExpiryAfterWaitingForARowLock()
    {
        var workspace = await Workspace(); await Add(workspace);
        await using var worker = Db(); var job = (await Queue(worker, TimeSpan.FromSeconds(3)).ClaimAsync(workspace, default))!;
        await using var locked = Db(); await using var transaction = await locked.Database.BeginTransactionAsync();
        await locked.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"AutomationJobs\" WHERE \"Id\" = {job.Id} FOR UPDATE");
        var completing = Queue(worker).CompleteAsync(workspace, job.Id, job.LeaseOwner!, default);
        await using var probe = Db();
        var waiting = false;
        for (var i = 0; i < 100 && !waiting; i++)
        {
            waiting = await probe.Database.SqlQuery<bool>($"""
                SELECT EXISTS (SELECT 1 FROM pg_stat_activity WHERE datname = current_database()
                    AND wait_event_type = 'Lock' AND query LIKE '%WITH owned AS MATERIALIZED%') AS "Value"
                """).SingleAsync();
            if (!waiting) await Task.Delay(20);
        }
        Assert.True(waiting, "Completion must have reached the locked row before expiry is tested.");
        // Wait on PostgreSQL's clock, not an assumption about the test machine's clock.
        await probe.Database.ExecuteSqlInterpolatedAsync($"""
            SELECT pg_sleep(GREATEST(0, EXTRACT(EPOCH FROM ("LeaseExpiresAt" - clock_timestamp())))::double precision + 0.01)
            FROM "AutomationJobs" WHERE "Id" = {job.Id}
            """);
        await transaction.CommitAsync();
        Assert.False(await completing.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(AutomationJobStatuses.Leased, (await probe.AutomationJobs.AsNoTracking().SingleAsync(x => x.Id == job.Id)).StatusCode);
        Assert.Equal(1, await Queue(probe).RecoverAsync(workspace, default));
    }
}
