using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using ProspectionCrm.Api.Data;
using ProspectionCrm.Api.Dtos.Integrations;
using ProspectionCrm.Api.Entities;

namespace ProspectionCrm.Api.Services.Integrations;

public sealed class IntegrationConflictException(string code) : Exception(code);
public enum IntegrationTransportOutcome { Acknowledged, Failed, Unknown }
public enum IntegrationReception { Recorded, AlreadyRecorded }

// Internal persistence only: no controller, transport, worker or business executor calls this store.
// All updates use untracked reads and SQL in their own transaction; no unrelated tracked changes are saved.
public sealed class IntegrationStore(ProspectionCrmDbContext db, TimeProvider clock)
{
    public async Task<IntegrationOperation> CreateProbeAsync(Guid workspaceId, CancellationToken token = default)
    {
        if (workspaceId == Guid.Empty) throw new ArgumentException("workspace-required", nameof(workspaceId));
        var op = new IntegrationOperation { Id = Guid.NewGuid(), WorkspaceId = workspaceId,
            CommandMessageId = Guid.NewGuid(), CorrelationId = Guid.NewGuid(), ProbeId = Guid.NewGuid(),
            Version = IntegrationContracts.Version, Type = IntegrationContracts.ProbeType,
            StatusCode = "pending", CreatedAt = Now() };
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "IntegrationOperations"
                ("Id","WorkspaceId","CommandMessageId","CorrelationId","ProbeId","Version","Type","StatusCode","Revision","CreatedAt")
            VALUES ({op.Id},{op.WorkspaceId},{op.CommandMessageId},{op.CorrelationId},{op.ProbeId},{op.Version},{op.Type},'pending',0,{op.CreatedAt})
            """, token);
        return op;
    }

    public Task<IntegrationOperation?> FindAsync(Guid workspaceId, Guid operationId, CancellationToken token = default) =>
        db.IntegrationOperations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == operationId && x.WorkspaceId == workspaceId, token);

    public async Task<IntegrationCommandEnvelope> CommandAsync(Guid workspaceId, Guid operationId, CancellationToken token = default)
    {
        var op = await FindAsync(workspaceId, operationId, token) ?? throw Conflict("operation-not-found");
        return new(op.Version, op.CommandMessageId, op.Id, op.CorrelationId, op.Type, new(op.ProbeId));
    }

    public async Task<IntegrationAttempt> BeginAttemptAsync(Guid workspaceId, Guid operationId, long expectedRevision,
        CancellationToken token = default)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var op = await LockAsync(workspaceId, operationId, token);
        ExpectRevision(op, expectedRevision);
        if (op.StatusCode is not ("pending" or "awaiting-result")) throw Conflict("operation-state-conflict");
        if (await db.IntegrationAttempts.AnyAsync(x => x.OperationId == op.Id && x.StatusCode == "started", token))
            throw Conflict("transport-attempt-active");
        var number = (await db.IntegrationAttempts.Where(x => x.OperationId == op.Id).MaxAsync(x => (int?)x.Number, token) ?? 0) + 1;
        var attempt = new IntegrationAttempt { Id = Guid.NewGuid(), OperationId = op.Id, Number = number,
            StatusCode = "started", StartedAt = Now(op.CreatedAt) };
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "IntegrationAttempts" ("Id","OperationId","Number","StatusCode","StartedAt")
            VALUES ({attempt.Id},{op.Id},{number},'started',{attempt.StartedAt});
            UPDATE "IntegrationOperations" SET "StatusCode" = 'awaiting-result', "Revision" = "Revision" + 1 WHERE "Id" = {op.Id};
            """, token);
        await transaction.CommitAsync(token);
        return attempt;
    }

    public async Task FinishAttemptAsync(Guid workspaceId, Guid operationId, Guid attemptId, IntegrationTransportOutcome outcome,
        CancellationToken token = default)
    {
        var status = outcome switch { IntegrationTransportOutcome.Acknowledged => "acknowledged",
            IntegrationTransportOutcome.Failed => "failed", IntegrationTransportOutcome.Unknown => "unknown",
            _ => throw new ArgumentOutOfRangeException(nameof(outcome)) };
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        _ = await LockAsync(workspaceId, operationId, token);
        var attempt = await db.IntegrationAttempts.AsNoTracking().SingleOrDefaultAsync(x => x.Id == attemptId && x.OperationId == operationId, token);
        if (attempt is null || attempt.StatusCode != "started") throw Conflict("transport-attempt-state-conflict");
        var now = Now(attempt.StartedAt);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "IntegrationAttempts" SET "StatusCode" = {status}, "FinishedAt" = {now} WHERE "Id" = {attemptId};
            """, token);
        // A transport ACK (even one arriving after the result) has no effect on logical success.
        await transaction.CommitAsync(token);
    }

    public async Task<IntegrationReception> ReceiveAsync(string json, CancellationToken token = default)
    {
        var envelope = IntegrationContracts.ParseResult(json);
        var canonical = IntegrationContracts.Serialize(envelope);
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var existing = await ReceiptAsync(envelope.MessageId, token);
        if (existing is not null) return Duplicate(existing, canonical, hash);

        // No workspace or business destination is accepted from the sender.
        var op = await db.IntegrationOperations.FromSqlInterpolated($"""
            SELECT * FROM "IntegrationOperations" WHERE "Id" = {envelope.OperationId} FOR UPDATE
            """).AsNoTracking().SingleOrDefaultAsync(token) ?? throw Conflict("operation-not-found");
        // Another receiver may have committed while this transaction was waiting for the row lock.
        existing = await ReceiptAsync(envelope.MessageId, token);
        if (existing is not null) return Duplicate(existing, canonical, hash);
        Match(op, envelope);
        if (op.StatusCode != "awaiting-result") throw Conflict("operation-state-conflict");
        var now = Now(op.CreatedAt);
        var inserted = await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "IntegrationReceipts" ("MessageId","OperationId","CanonicalContent","ContentSha256","ReceivedAt")
            VALUES ({envelope.MessageId},{op.Id},{canonical},{hash},{now})
            ON CONFLICT ("MessageId") DO NOTHING
            """, token);
        if (inserted == 0)
            return Duplicate(await ReceiptAsync(envelope.MessageId, token) ?? throw Conflict("receipt-concurrency-conflict"), canonical, hash);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "IntegrationOperations" SET "StatusCode" = 'result-received', "Revision" = "Revision" + 1 WHERE "Id" = {op.Id}
            """, token);
        await transaction.CommitAsync(token);
        return IntegrationReception.Recorded;
    }

    // A new scope/process can enumerate durable results after a crash, without any transport replay.
    public Task<List<IntegrationReceipt>> PendingReceiptsAsync(Guid workspaceId, int limit = 100, CancellationToken token = default)
    {
        if (limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit));
        return (from receipt in db.IntegrationReceipts.AsNoTracking()
                join op in db.IntegrationOperations.AsNoTracking() on receipt.OperationId equals op.Id
                where op.WorkspaceId == workspaceId && receipt.ProcessedAt == null
                orderby receipt.ReceivedAt, receipt.MessageId
                select receipt).Take(limit).ToListAsync(token);
    }

    public async Task ProcessProbeAsync(Guid workspaceId, Guid operationId, long expectedRevision, CancellationToken token = default)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var op = await LockAsync(workspaceId, operationId, token);
        ExpectRevision(op, expectedRevision);
        if (op.StatusCode != "result-received") throw Conflict("operation-state-conflict");
        var receipt = await db.IntegrationReceipts.AsNoTracking().SingleAsync(x => x.OperationId == op.Id, token);
        if (receipt.ProcessedAt is not null) throw Conflict("receipt-already-processed");
        var result = IntegrationContracts.ParseResult(receipt.CanonicalContent);
        Match(op, result);
        var now = Now(receipt.ReceivedAt);
        // This probe has no business effect. Future business processing must define its own atomicity.
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "IntegrationReceipts" SET "ProcessedAt" = {now} WHERE "MessageId" = {receipt.MessageId};
            UPDATE "IntegrationOperations" SET "StatusCode" = {result.Payload.Outcome}, "CompletedAt" = {now},
                "Revision" = "Revision" + 1 WHERE "Id" = {op.Id};
            """, token);
        await transaction.CommitAsync(token);
    }

    private Task<IntegrationReceipt?> ReceiptAsync(Guid messageId, CancellationToken token) =>
        db.IntegrationReceipts.AsNoTracking().SingleOrDefaultAsync(x => x.MessageId == messageId, token);

    private async Task<IntegrationOperation> LockAsync(Guid workspaceId, Guid operationId, CancellationToken token) =>
        await db.IntegrationOperations.FromSqlInterpolated($"""
            SELECT * FROM "IntegrationOperations" WHERE "Id" = {operationId} AND "WorkspaceId" = {workspaceId} FOR UPDATE
            """).AsNoTracking().SingleOrDefaultAsync(token) ?? throw Conflict("operation-not-found");

    private static void Match(IntegrationOperation op, IntegrationResultEnvelope result)
    {
        if (result.OperationId != op.Id || result.CorrelationId != op.CorrelationId || result.CommandMessageId != op.CommandMessageId
            || result.Version != op.Version || result.Type != op.Type || result.Payload.ProbeId != op.ProbeId)
            throw Conflict("operation-context-conflict");
    }

    private static IntegrationReception Duplicate(IntegrationReceipt receipt, string canonical, string hash)
    {
        // Compare the complete canonical content as well as its digest: no hash-only equality assumption.
        if (!StringComparer.Ordinal.Equals(receipt.CanonicalContent, canonical) || receipt.ContentSha256 != hash)
            throw Conflict("message-content-conflict");
        return IntegrationReception.AlreadyRecorded;
    }

    private static void ExpectRevision(IntegrationOperation op, long expected)
    { if (op.Revision != expected) throw Conflict("operation-revision-conflict"); }
    private static IntegrationConflictException Conflict(string code) => new(code);
    private DateTimeOffset Now(DateTimeOffset floor = default)
    { var now = clock.GetUtcNow().ToUniversalTime(); return now < floor ? floor : now; }
}
