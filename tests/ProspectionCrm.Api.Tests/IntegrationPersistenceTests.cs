using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using ProspectionCrm.Api.Data;
using ProspectionCrm.Api.Dtos.Integrations;
using ProspectionCrm.Api.Entities;
using ProspectionCrm.Api.Services.Integrations;
using Xunit;

namespace ProspectionCrm.Api.Tests;

public sealed class IntegrationPersistenceTests(AutomationJobDatabase database) : IClassFixture<AutomationJobDatabase>
{
    private static IntegrationStore Store(ProspectionCrmDbContext db) => new(db, TimeProvider.System);

    private async Task<IntegrationOperation> Operation(bool start = true)
    {
        await using var db = database.Db();
        var workspace = new Workspace { Name = "Integration probe", TimeZoneId = "UTC",
            OwnerUser = new() { Email = $"{Guid.NewGuid():N}@example.invalid" } };
        db.Add(workspace); await db.SaveChangesAsync();
        var op = await Store(db).CreateProbeAsync(workspace.Id);
        if (start) await Store(db).BeginAttemptAsync(workspace.Id, op.Id, op.Revision);
        return (await Store(db).FindAsync(workspace.Id, op.Id))!;
    }

    private static IntegrationResultEnvelope Result(IntegrationOperation op, string outcome = "succeeded") =>
        new(op.Version, Guid.NewGuid(), op.Id, op.CorrelationId, op.CommandMessageId, op.Type, new(op.ProbeId, outcome));

    [Theory]
    [InlineData("succeeded")]
    [InlineData("failed")]
    public async Task RestartFindsDurableReceiptAndFinishesOnlyAfterExplicitAtomicProcessing(string outcome)
    {
        var op = await Operation(); var json = IntegrationContracts.Serialize(Result(op, outcome));
        await using (var db = database.Db())
        {
            var store = Store(db);
            var attempt = await db.IntegrationAttempts.SingleAsync(x => x.OperationId == op.Id);
            await store.FinishAttemptAsync(op.WorkspaceId, op.Id, attempt.Id, IntegrationTransportOutcome.Acknowledged);
            Assert.Equal("awaiting-result", (await store.FindAsync(op.WorkspaceId, op.Id))!.StatusCode);
            Assert.Equal(IntegrationReception.Recorded, await store.ReceiveAsync(json));
            var received = (await store.FindAsync(op.WorkspaceId, op.Id))!;
            Assert.Equal("result-received", received.StatusCode); Assert.Null(received.CompletedAt);
        }
        // Discard the original DbContext/store, as on process restart, then resume from the database.
        await using (var db = database.Db())
        {
            var store = Store(db);
            var pending = Assert.Single(await store.PendingReceiptsAsync(op.WorkspaceId));
            Assert.Null(pending.ProcessedAt); Assert.Equal(op.Id, pending.OperationId);
            var received = (await store.FindAsync(op.WorkspaceId, op.Id))!;
            await store.ProcessProbeAsync(op.WorkspaceId, op.Id, received.Revision);
        }
        await using var verify = database.Db();
        var completed = (await Store(verify).FindAsync(op.WorkspaceId, op.Id))!;
        Assert.Equal(outcome, completed.StatusCode); Assert.NotNull(completed.CompletedAt);
        Assert.NotNull((await verify.IntegrationReceipts.SingleAsync(x => x.OperationId == op.Id)).ProcessedAt);
        Assert.Empty(await Store(verify).PendingReceiptsAsync(op.WorkspaceId));
        Assert.Equal(IntegrationReception.AlreadyRecorded, await Store(verify).ReceiveAsync(json));
        await Conflict("operation-state-conflict", () => Store(verify).ProcessProbeAsync(op.WorkspaceId, op.Id, completed.Revision));
        await Conflict("operation-state-conflict", () => Store(verify).BeginAttemptAsync(op.WorkspaceId, op.Id, completed.Revision));
        Assert.Empty(await verify.SourceExecutions.Where(x => x.WorkspaceId == op.WorkspaceId).ToListAsync());
        Assert.Empty(await verify.AutomationExecutions.Where(x => x.WorkspaceId == op.WorkspaceId).ToListAsync());
        Assert.Empty(await verify.EmailMessages.Where(x => x.WorkspaceId == op.WorkspaceId).ToListAsync());
        Assert.Empty(await verify.CalendarEvents.Where(x => x.WorkspaceId == op.WorkspaceId).ToListAsync());
    }

    [Fact]
    public async Task CanonicalDuplicatesAreIdempotentAndChangedContentOrSecondResultCannotOverwrite()
    {
        var op = await Operation(); var result = Result(op); var json = IntegrationContracts.Serialize(result);
        await using var db = database.Db(); var store = Store(db);
        Assert.Equal(IntegrationReception.Recorded, await store.ReceiveAsync(json));
        var original = JsonNode.Parse(json)!.AsObject();
        var reordered = new JsonObject(original.Reverse().Select(x => new KeyValuePair<string, JsonNode?>(x.Key, x.Value?.DeepClone())));
        reordered["messageId"] = result.MessageId.ToString("D").ToUpperInvariant();
        Assert.Equal(IntegrationReception.AlreadyRecorded, await store.ReceiveAsync("\n " + reordered.ToJsonString() + " \n"));
        await Conflict("message-content-conflict", () => store.ReceiveAsync(IntegrationContracts.Serialize(result with { Payload = result.Payload with { Outcome = "failed" } })));
        await Conflict("operation-state-conflict", () => store.ReceiveAsync(IntegrationContracts.Serialize(result with { MessageId = Guid.NewGuid() })));
        var receipt = await db.IntegrationReceipts.SingleAsync(x => x.OperationId == op.Id);
        Assert.Equal(json, receipt.CanonicalContent);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json))), receipt.ContentSha256);
        Assert.Equal(2, (await store.FindAsync(op.WorkspaceId, op.Id))!.Revision);
    }

    [Fact]
    public async Task ContextCannotBeRedirectedEvenToAnotherExistingWorkspace()
    {
        var op = await Operation(); var other = await Operation(); var result = Result(op);
        await using var db = database.Db(); var store = Store(db);
        foreach (var invalid in new[] { result with { OperationId = other.Id }, result with { CorrelationId = Guid.NewGuid() },
                     result with { CommandMessageId = Guid.NewGuid() }, result with { Payload = new(Guid.NewGuid(), "succeeded") } })
            await Conflict("operation-context-conflict", () => store.ReceiveAsync(IntegrationContracts.Serialize(invalid)));
        await Conflict("operation-not-found", () => store.ReceiveAsync(IntegrationContracts.Serialize(result with { OperationId = Guid.NewGuid() })));
        Assert.Null(await store.FindAsync(other.WorkspaceId, op.Id));
        await Conflict("operation-not-found", () => store.BeginAttemptAsync(other.WorkspaceId, op.Id, op.Revision));
        Assert.Equal(IntegrationReception.Recorded, await store.ReceiveAsync(IntegrationContracts.Serialize(result)));
        Assert.Empty(await store.PendingReceiptsAsync(other.WorkspaceId));
        await Conflict("operation-not-found", () => store.ProcessProbeAsync(other.WorkspaceId, op.Id, 2));
        Assert.Single(await store.PendingReceiptsAsync(op.WorkspaceId));
        // A message ID is global across workspaces, not scoped to an untrusted payload.
        await Conflict("message-content-conflict", () => store.ReceiveAsync(IntegrationContracts.Serialize(Result(other) with { MessageId = result.MessageId })));
        Assert.Equal("awaiting-result", (await store.FindAsync(other.WorkspaceId, other.Id))!.StatusCode);
    }

    [Fact]
    public async Task RetriesKeepTheSameLogicalCommandAndRequireExplicitResolutionOfStartedAttempts()
    {
        var op = await Operation(false);
        await using var db = database.Db(); var store = Store(db);
        var command = await store.CommandAsync(op.WorkspaceId, op.Id);
        await Conflict("operation-state-conflict", () => store.ReceiveAsync(IntegrationContracts.Serialize(Result(op))));
        await Conflict("operation-state-conflict", () => store.ProcessProbeAsync(op.WorkspaceId, op.Id, 0));
        var first = await store.BeginAttemptAsync(op.WorkspaceId, op.Id, 0);
        await Conflict("operation-revision-conflict", () => store.BeginAttemptAsync(op.WorkspaceId, op.Id, 0));
        await Conflict("transport-attempt-active", () => store.BeginAttemptAsync(op.WorkspaceId, op.Id, 1));
        await store.FinishAttemptAsync(op.WorkspaceId, op.Id, first.Id, IntegrationTransportOutcome.Unknown);
        await Conflict("transport-attempt-state-conflict", () => store.FinishAttemptAsync(op.WorkspaceId, op.Id, first.Id, IntegrationTransportOutcome.Acknowledged));
        var second = await store.BeginAttemptAsync(op.WorkspaceId, op.Id, 1);
        Assert.Equal(2, second.Number); Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(command, await store.CommandAsync(op.WorkspaceId, op.Id));
        await store.FinishAttemptAsync(op.WorkspaceId, op.Id, second.Id, IntegrationTransportOutcome.Failed);
        Assert.Equal("awaiting-result", (await store.FindAsync(op.WorkspaceId, op.Id))!.StatusCode);
    }

    [Fact]
    public async Task ConcurrentDuplicateDeliveriesWaitOnPostgresAndCreateExactlyOneReceipt()
    {
        var op = await Operation(); var json = IntegrationContracts.Serialize(Result(op));
        var results = await Race(op.Id, 8, async db => (await Store(db).ReceiveAsync(json)).ToString());
        Assert.Single(results, x => x == "Recorded"); Assert.Equal(7, results.Count(x => x == "AlreadyRecorded"));
        await using var verify = database.Db();
        Assert.Equal(1, await verify.IntegrationReceipts.CountAsync(x => x.OperationId == op.Id));
        Assert.Equal(2, (await Store(verify).FindAsync(op.WorkspaceId, op.Id))!.Revision);
    }

    [Fact]
    public async Task ConcurrentDifferentContentsWithSameIdConflictAndConcurrentProcessingRejectsStaleRevision()
    {
        var op = await Operation(); var result = Result(op); var turn = -1;
        var results = await Race(op.Id, 2, async db =>
        {
            var outcome = Interlocked.Increment(ref turn) == 0 ? "succeeded" : "failed";
            return (await Store(db).ReceiveAsync(IntegrationContracts.Serialize(result with { Payload = result.Payload with { Outcome = outcome } }))).ToString();
        });
        Assert.Contains("Recorded", results); Assert.Contains("message-content-conflict", results);
        var processed = await Race(op.Id, 2, async db => { await Store(db).ProcessProbeAsync(op.WorkspaceId, op.Id, 2); return "processed"; });
        Assert.Contains("processed", processed); Assert.Contains("operation-revision-conflict", processed);
        await using var verify = database.Db();
        var receipt = await verify.IntegrationReceipts.SingleAsync(x => x.OperationId == op.Id);
        Assert.Equal(IntegrationContracts.ParseResult(receipt.CanonicalContent).Payload.Outcome,
            (await Store(verify).FindAsync(op.WorkspaceId, op.Id))!.StatusCode);
    }

    [Fact]
    public async Task ConcurrentStartsCannotCreateTwoAttempts()
    {
        var op = await Operation(false);
        var results = await Race(op.Id, 2, async db => { await Store(db).BeginAttemptAsync(op.WorkspaceId, op.Id, 0); return "started"; });
        Assert.Contains("started", results); Assert.Contains("operation-revision-conflict", results);
        await using var verify = database.Db();
        Assert.Equal(1, await verify.IntegrationAttempts.CountAsync(x => x.OperationId == op.Id));
    }

    [Fact]
    public async Task DatabaseConstraintsProtectReceiptIdentityContextAndActiveAttempt()
    {
        var op = await Operation();
        await using var db = database.Db();
        await Store(db).ReceiveAsync(IntegrationContracts.Serialize(Result(op)));
        var receipt = await db.IntegrationReceipts.AsNoTracking().SingleAsync(x => x.OperationId == op.Id);
        var duplicate = new IntegrationReceipt { MessageId = receipt.MessageId, OperationId = receipt.OperationId,
            CanonicalContent = receipt.CanonicalContent, ContentSha256 = receipt.ContentSha256, ReceivedAt = receipt.ReceivedAt };
        await Constraint(duplicate, PostgresErrorCodes.UniqueViolation);
        duplicate.MessageId = Guid.NewGuid(); await Constraint(duplicate, PostgresErrorCodes.UniqueViolation);
        duplicate.OperationId = Guid.NewGuid(); await Constraint(duplicate, PostgresErrorCodes.ForeignKeyViolation);
        await Constraint(new IntegrationAttempt { Id = Guid.NewGuid(), OperationId = op.Id, Number = 2,
            StatusCode = "started", StartedAt = DateTimeOffset.UtcNow }, PostgresErrorCodes.UniqueViolation);
        var error = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "IntegrationOperations" SET "StatusCode" = 'succeeded' WHERE "Id" = {op.Id}
            """));
        Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
    }

    [Fact]
    public async Task ResultCanArriveBeforeTransportAckAndLateAckCannotChangeSuccess()
    {
        var op = await Operation();
        await using var db = database.Db(); var store = Store(db);
        await store.ReceiveAsync(IntegrationContracts.Serialize(Result(op)));
        await store.ProcessProbeAsync(op.WorkspaceId, op.Id, 2);
        var attempt = await db.IntegrationAttempts.SingleAsync(x => x.OperationId == op.Id);
        await store.FinishAttemptAsync(op.WorkspaceId, op.Id, attempt.Id, IntegrationTransportOutcome.Acknowledged);
        var completed = (await store.FindAsync(op.WorkspaceId, op.Id))!;
        Assert.Equal("succeeded", completed.StatusCode); Assert.Equal(3, completed.Revision);
    }

    [Fact]
    public async Task DatabaseFailureRollsBackReceiptAndProcessingTogetherWithTheirOperationTransitions()
    {
        var op = await Operation(); var json = IntegrationContracts.Serialize(Result(op));
        await using var db = database.Db(); var store = Store(db);
        // Inject failures after the first write of each transaction, only in this disposable database.
        await db.Database.ExecuteSqlRawAsync("""
            ALTER TABLE "IntegrationOperations" ADD CONSTRAINT "CK_IntegrationTests_Fault"
            CHECK ("StatusCode" <> 'result-received') NOT VALID
            """);
        try
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() => store.ReceiveAsync(json));
            Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
            Assert.False(await db.IntegrationReceipts.AnyAsync(x => x.OperationId == op.Id));
            Assert.Equal("awaiting-result", (await store.FindAsync(op.WorkspaceId, op.Id))!.StatusCode);
        }
        finally { await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"IntegrationOperations\" DROP CONSTRAINT \"CK_IntegrationTests_Fault\""); }
        await store.ReceiveAsync(json);
        await db.Database.ExecuteSqlRawAsync("""
            ALTER TABLE "IntegrationOperations" ADD CONSTRAINT "CK_IntegrationTests_Fault"
            CHECK ("StatusCode" NOT IN ('succeeded','failed')) NOT VALID
            """);
        try
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() => store.ProcessProbeAsync(op.WorkspaceId, op.Id, 2));
            Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
            Assert.Null(Assert.Single(await store.PendingReceiptsAsync(op.WorkspaceId)).ProcessedAt);
            var received = (await store.FindAsync(op.WorkspaceId, op.Id))!;
            Assert.Equal("result-received", received.StatusCode); Assert.Equal(2, received.Revision);
        }
        finally { await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"IntegrationOperations\" DROP CONSTRAINT \"CK_IntegrationTests_Fault\""); }
        await store.ProcessProbeAsync(op.WorkspaceId, op.Id, 2);
        Assert.Empty(await store.PendingReceiptsAsync(op.WorkspaceId));
    }

    private async Task Constraint(object entity, string sqlState)
    {
        await using var db = database.Db(); db.Add(entity);
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Equal(sqlState, Assert.IsType<PostgresException>(error.InnerException).SqlState);
    }

    private static async Task Conflict(string code, Func<Task> action) =>
        Assert.Equal(code, (await Assert.ThrowsAsync<IntegrationConflictException>(action)).Message);

    private async Task<string[]> Race(Guid operationId, int count, Func<ProspectionCrmDbContext, Task<string>> action)
    {
        await using var blocker = database.Db();
        await using var transaction = await blocker.Database.BeginTransactionAsync();
        _ = await blocker.IntegrationOperations.FromSqlInterpolated($"""
            SELECT * FROM "IntegrationOperations" WHERE "Id" = {operationId} FOR UPDATE
            """).AsNoTracking().SingleAsync();
        var tasks = Enumerable.Range(0, count).Select(async _ =>
        {
            await using var db = database.Db();
            try { return await action(db); }
            catch (IntegrationConflictException error) { return error.Message; }
        }).ToArray();
        var timer = Stopwatch.StartNew(); var waiting = 0;
        try
        {
            // Prove real overlapping SQL transactions, not merely sequential calls inside Task.WhenAll.
            while (waiting < count && timer.Elapsed < TimeSpan.FromSeconds(15))
            {
                await blocker.Database.ExecuteSqlRawAsync("SELECT pg_stat_clear_snapshot()");
                waiting = await blocker.Database.SqlQuery<int>($"""
                    SELECT count(*)::int AS "Value" FROM pg_stat_activity
                    WHERE datname = current_database() AND wait_event_type = 'Lock'
                      AND query LIKE '%IntegrationOperations%'
                    """).SingleAsync();
                if (waiting < count) await Task.Delay(25);
            }
        }
        finally { await transaction.RollbackAsync(); }
        var results = await Task.WhenAll(tasks);
        Assert.Equal(count, waiting);
        return results;
    }
}
