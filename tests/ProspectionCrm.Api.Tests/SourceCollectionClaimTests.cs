using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using ProspectionCrm.Api.Data;
using ProspectionCrm.Api.Entities;
using ProspectionCrm.Api.Services.Collection;
using Xunit;

namespace ProspectionCrm.Api.Tests;

public sealed class SourceCollectionClaimTests : PersistentSourceIdentityFixture
{
    private static SourceCollectionJobQueue Queue(ProspectionCrmDbContext db) => new(db, Options.Create(new SourceCollectionWorkerOptions()));
    private async Task<SourceCollectionJob> Add(Setup setup, DateTimeOffset? available = null)
    {
        await using var db = Db();
        var stage = new PipelineStage { PipelineId = setup.Pipeline, Name = Guid.NewGuid().ToString(), CategoryCode = "active",
            SortOrder = await db.PipelineStages.Where(x => x.PipelineId == setup.Pipeline).MaxAsync(x => x.SortOrder) + 1 };
        var job = new SourceCollectionJob { WorkspaceId = setup.Workspace, SavedSearchId = setup.Search,
            PipelineId = setup.Pipeline, PipelineStage = stage, TriggerTypeCode = "manual", StatusCode = "queued",
            EnqueuedAt = DateTimeOffset.UtcNow.AddHours(-2), AvailableAt = available ?? DateTimeOffset.UtcNow.AddHours(-1) };
        db.Add(job); await db.SaveChangesAsync(); return job;
    }

    [Fact]
    public async Task ConcurrentClaimersOwnOneJobExactlyOnce()
    {
        var setup = await PrepareAsync(); var job = await Add(setup);
        await using var a = Db(); await using var b = Db();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var arrivals = 0;
        async Task<SourceCollectionJob?> Claim(ProspectionCrmDbContext db)
        {
            if (Interlocked.Increment(ref arrivals) == 2) ready.SetResult();
            await ready.Task; return await Queue(db).ClaimAsync(default);
        }
        var results = await Task.WhenAll(Claim(a), Claim(b)).WaitAsync(TimeSpan.FromSeconds(20));
        var claimed = Assert.Single(results.OfType<SourceCollectionJob>());
        Assert.Equal(job.Id, claimed.Id); Assert.Equal(1, claimed.AttemptCount);
        Assert.Equal("running", claimed.StatusCode); Assert.NotNull(claimed.LeaseToken); Assert.NotEqual(Guid.Empty, claimed.LeaseToken);
        Assert.Equal(TimeSpan.FromSeconds(300), claimed.LeaseExpiresAt - claimed.StartedAt);
        Assert.Null(await Queue(a).ClaimAsync(default));
        Assert.Equal(1, (await b.SourceCollectionJobs.AsNoTracking().SingleAsync()).AttemptCount);
    }

    [Fact]
    public async Task ConcurrentClaimersCanOwnDifferentJobs()
    {
        var setup = await PrepareAsync(); var first = await Add(setup); var second = await Add(setup);
        await using var a = Db(); await using var b = Db();
        var claimed = await Task.WhenAll(Queue(a).ClaimAsync(default), Queue(b).ClaimAsync(default))
            .WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(new[] { first.Id, second.Id }.Order(), claimed.Select(x => x!.Id).Order());
        Assert.NotEqual(claimed[0]!.LeaseToken, claimed[1]!.LeaseToken);
        Assert.All(claimed, job => Assert.Equal(1, job!.AttemptCount));
    }

    [Fact]
    public async Task LockedFirstJobIsSkippedAndPriorityOrderIsStable()
    {
        var setup = await PrepareAsync(); var first = await Add(setup, DateTimeOffset.UtcNow.AddMinutes(-50));
        var second = await Add(setup, DateTimeOffset.UtcNow.AddMinutes(-40));
        var third = await Add(setup, second.AvailableAt);
        // Explicit UUID ordering is asserted from PostgreSQL, independently of insertion order.
        await using var db = Db();
        await db.SourceCollectionJobs.ExecuteUpdateAsync(s => s.SetProperty(x => x.EnqueuedAt, first.EnqueuedAt));
        var order = await db.SourceCollectionJobs.OrderBy(x => x.AvailableAt).ThenBy(x => x.EnqueuedAt).ThenBy(x => x.Id).Select(x => x.Id).ToArrayAsync();
        await using var connection = new NpgsqlConnection(Postgres.GetConnectionString()); await connection.OpenAsync();
        await using var tx = await connection.BeginTransactionAsync();
        await using var command = new NpgsqlCommand("SELECT 1 FROM \"SourceCollectionJobs\" WHERE \"Id\" = @id FOR UPDATE", connection, tx);
        command.Parameters.AddWithValue("id", first.Id); await command.ExecuteScalarAsync();
        var claimed = await Queue(db).ClaimAsync(default).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(order[1], claimed!.Id);
        await tx.CommitAsync();
        Assert.Equal(first.Id, (await Queue(db).ClaimAsync(default))!.Id);
        Assert.Equal(order[2], (await Queue(db).ClaimAsync(default))!.Id);
        Assert.Null(await Queue(db).ClaimAsync(default));
    }

    [Theory]
    [InlineData("future")]
    [InlineData("cancelled")]
    [InlineData("failed")]
    [InlineData("succeeded")]
    [InlineData("valid-lease")]
    [InlineData("expired-lease")]
    public async Task OnlyAvailableQueuedJobsAreEligible(string state)
    {
        var setup = await PrepareAsync(); var original = await Add(setup);
        await using var db = Db(); var job = await db.SourceCollectionJobs.SingleAsync();
        if (state == "future") job.AvailableAt = DateTimeOffset.UtcNow.AddDays(1);
        else if (state.EndsWith("lease"))
        {
            job.StatusCode = "running"; job.StartedAt = job.AvailableAt; job.AttemptCount = 1; job.LeaseToken = Guid.NewGuid();
            job.LeaseExpiresAt = state == "valid-lease" ? DateTimeOffset.UtcNow.AddMinutes(5) : job.StartedAt.Value.AddMinutes(1);
        }
        else
        {
            job.StatusCode = state; job.StartedAt = job.AvailableAt; job.FinishedAt = DateTimeOffset.UtcNow;
            job.ErrorCode = state == "failed" ? "Failure" : null; job.SourceExecutionId = state == "succeeded" ? setup.Execution : null;
        }
        await db.SaveChangesAsync();
        Assert.Null(await Queue(db).ClaimAsync(default)); Assert.Null(await Queue(db).ClaimAsync(default));
        var read = await db.SourceCollectionJobs.AsNoTracking().SingleAsync();
        Assert.Equal(original.Id, read.Id); Assert.Equal(job.StatusCode, read.StatusCode);
        Assert.Equal(job.AttemptCount, read.AttemptCount); Assert.Equal(job.LeaseToken, read.LeaseToken);
    }

    [Fact]
    public async Task CompletionRequiresWorkspaceAndTokenAndReleasesActiveSlot()
    {
        var setup = await PrepareAsync(); var original = await Add(setup);
        await using var db = Db(); var queue = Queue(db); var job = (await queue.ClaimAsync(default))!;
        Assert.False(await queue.CompleteAsync(job.Id, Guid.NewGuid(), job.LeaseToken!.Value, "Failure", default));
        Assert.False(await queue.CompleteAsync(job.Id, job.WorkspaceId, Guid.NewGuid(), "Failure", default));
        Assert.True(await queue.CompleteAsync(job.Id, job.WorkspaceId, job.LeaseToken.Value, "UpstreamTimeout", default));
        Assert.False(await queue.CompleteAsync(job.Id, job.WorkspaceId, job.LeaseToken.Value, "Failure", default));
        var stored = await db.SourceCollectionJobs.AsNoTracking().SingleAsync();
        Assert.Equal("failed", stored.StatusCode); Assert.Equal("UpstreamTimeout", stored.ErrorCode);
        Assert.NotNull(stored.FinishedAt); Assert.Null(stored.LeaseToken); Assert.Null(stored.LeaseExpiresAt); Assert.Equal(1, stored.AttemptCount);
        Assert.Null(await queue.ClaimAsync(default));
        db.Add(new SourceCollectionJob { WorkspaceId = setup.Workspace, SavedSearchId = setup.Search,
            PipelineId = setup.Pipeline, PipelineStageId = original.PipelineStageId, TriggerTypeCode = "manual",
            StatusCode = "queued", EnqueuedAt = DateTimeOffset.UtcNow.AddSeconds(-1), AvailableAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync(); Assert.NotEqual(job.Id, (await queue.ClaimAsync(default))!.Id);
    }

    [Fact]
    public async Task CommittedSuccessWinsOverLateCancellationEvenAfterLeaseExpiry()
    {
        var setup = await PrepareAsync(); await Add(setup);
        await using var db = Db(); var queue = Queue(db); var job = (await queue.ClaimAsync(default))!;
        await db.SourceCollectionJobs.ExecuteUpdateAsync(s => s.SetProperty(x => x.StartedAt, job.AvailableAt)
            .SetProperty(x => x.LeaseExpiresAt, job.AvailableAt.AddSeconds(1)).SetProperty(x => x.SourceExecutionId, setup.Execution));
        Assert.True(await queue.CompleteAsync(job.Id, setup.Workspace, job.LeaseToken!.Value, "CollectionWorkerStopping", default));
        var stored = await db.SourceCollectionJobs.AsNoTracking().SingleAsync();
        Assert.Equal("succeeded", stored.StatusCode); Assert.Null(stored.ErrorCode); Assert.Null(stored.LeaseToken);
    }
}
