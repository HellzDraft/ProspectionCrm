using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using ProspectionCrm.Api.Data;
using ProspectionCrm.Api.Dtos.EmailMessages;
using ProspectionCrm.Api.Dtos.Integrations;
using ProspectionCrm.Api.Entities;
using ProspectionCrm.Api.Services.Integrations;

namespace ProspectionCrm.Api.Services.Email;

public sealed class EmailSyncService(ProspectionCrmDbContext db, EmailAccountService accounts, InboundEmailImportService importer)
{
    public async Task<EmailSyncJobDto?> FindAsync(Guid workspace, Guid id, CancellationToken token)
    {
        var job = await db.EmailSyncJobs.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.WorkspaceId == workspace, token);
        return job is null ? null : Dto(job);
    }

    public async Task<EmailSyncJobDto> EnqueueAsync(Guid workspace, Guid accountId, int maxItems, CancellationToken token)
    {
        if (maxItems is < 1 or > 100) throw new EmailSyncException("EmailSyncInvalidLimit", 400);
        await using var tx = await db.Database.BeginTransactionAsync(token);
        await EmailSyncLocks.WorkspaceAsync(db, workspace, token);
        var account = await EmailSyncLocks.AccountAsync(db, workspace, accountId, token);
        RequireActive(account); accounts.RequireConnection(workspace, account.ConnectionId);
        if (await db.EmailSyncJobs.AnyAsync(x => x.EmailAccountId == accountId && x.FinishedAt == null, token))
            throw new EmailSyncException("EmailSyncAlreadyActive");
        var state = await EmailSyncLocks.StateAsync(db, accountId, token);
        var now = await EmailSyncLocks.Now(db, token);
        var op = Guid.NewGuid(); var job = Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "IntegrationOperations" ("Id","WorkspaceId","ConnectionId","CommandMessageId","CorrelationId","ProbeId","Version","Type","StatusCode","Revision","CreatedAt")
            VALUES ({op},{workspace},{account.ConnectionId},{Guid.NewGuid()},{Guid.NewGuid()},{Guid.Empty},1,'email.sync','pending',0,{now});
            INSERT INTO "EmailSyncJobs" ("Id","WorkspaceId","EmailAccountId","ConnectionId","TriggerTypeCode","StatusCode","PreviousCursor","StateRevision","AccountRevision","MaxItems","IntegrationOperationId","OperationType","EnqueuedAt","CreatedCount","ExistingCount")
            VALUES ({job},{workspace},{accountId},{account.ConnectionId},'manual','queued',{state.Cursor},{state.Revision},{account.Revision},{maxItems},{op},'email.sync',{now},0,0)
            """, token);
        var result = await FindAsync(workspace, job, token);
        await tx.CommitAsync(token); return result!;
    }

    internal async Task<(EmailSyncCommand Command, Guid Attempt)> BeginAsync(Guid workspace, Guid id, CancellationToken token)
    {
        await using var tx = await db.Database.BeginTransactionAsync(token);
        await EmailSyncLocks.WorkspaceAsync(db, workspace, token);
        var (account, job, op) = await EmailSyncLocks.JobAsync(db, workspace, id, token);
        if (job.StatusCode != "queued" || op.StatusCode != "pending") throw new EmailSyncException("EmailDispatchNotReady");
        var state = await EmailSyncLocks.StateAsync(db, account.Id, token);
        var invalid = ContextError(account, job, state);
        if (invalid is not null)
        {
            await FinishAsync(job, op, null, invalid, token: token); await tx.CommitAsync(token);
            throw new EmailSyncException(invalid);
        }
        var now = await EmailSyncLocks.Now(db, token); var attempt = Guid.NewGuid();
        var command = new EmailSyncCommand(op.Version, op.CommandMessageId, op.Id, op.CorrelationId, op.Type,
            new(account.Id, job.PreviousCursor, job.MaxItems));
        _ = EmailSyncContracts.Serialize(command);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "IntegrationAttempts" ("Id","OperationId","Number","StatusCode","StartedAt") VALUES ({attempt},{op.Id},1,'started',{now});
            UPDATE "IntegrationOperations" SET "StatusCode" = 'awaiting-result', "Revision" = "Revision" + 1 WHERE "Id" = {op.Id};
            UPDATE "EmailSyncJobs" SET "StatusCode" = 'running', "StartedAt" = {now} WHERE "Id" = {job.Id};
            UPDATE "EmailSyncStates" SET "LastAttemptAt" = {now}, "UpdatedAt" = {now} WHERE "EmailAccountId" = {account.Id}
            """, token);
        await tx.CommitAsync(token); return (command, attempt);
    }

    internal async Task CompleteDispatchAsync(Guid workspace, Guid id, Guid attemptId, IntegrationTransportOutcome outcome, CancellationToken token)
    {
        await using var tx = await db.Database.BeginTransactionAsync(token);
        var (_, job, op) = await EmailSyncLocks.JobAsync(db, workspace, id, token);
        var status = outcome switch { IntegrationTransportOutcome.Acknowledged => "acknowledged", IntegrationTransportOutcome.Failed => "failed", _ => "unknown" };
        var updated = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "IntegrationAttempts" SET "StatusCode" = {status}, "FinishedAt" = GREATEST(statement_timestamp(), "StartedAt")
            WHERE "Id" = {attemptId} AND "OperationId" = {op.Id} AND "StatusCode" = 'started'
            """, token);
        // Callback, cancellation or explicit recovery can win while transport is in flight.
        if (updated != 0 && job.StatusCode == "running")
        {
            if (outcome == IntegrationTransportOutcome.Failed) await FinishAsync(job, op, null, "EmailDispatchFailed", token: token);
            else
            {
                var jobStatus = outcome == IntegrationTransportOutcome.Acknowledged ? "awaiting-result" : "dispatch-unknown";
                string? code = jobStatus == "dispatch-unknown" ? "EmailDispatchUnknown" : null;
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    UPDATE "EmailSyncJobs" SET "StatusCode" = {jobStatus}, "ErrorCode" = {code} WHERE "Id" = {id}
                    """, token);
            }
        }
        await tx.CommitAsync(token);
    }

    // Explicit recovery only; never retransmits an uncertain command. Safe even if a late ACK arrives.
    public async Task ResolveUnknownAsync(Guid workspace, Guid id, CancellationToken token)
    {
        await using var tx = await db.Database.BeginTransactionAsync(token);
        var (_, job, op) = await EmailSyncLocks.JobAsync(db, workspace, id, token);
        var now = await EmailSyncLocks.Now(db, token);
        var attempt = await db.IntegrationAttempts.AsNoTracking().SingleOrDefaultAsync(x => x.OperationId == op.Id && x.StatusCode == "started", token);
        if (attempt is null || now - attempt.StartedAt < TimeSpan.FromMinutes(2)) throw new EmailSyncException("EmailAttemptNotRecoverable");
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "IntegrationAttempts" SET "StatusCode" = 'unknown', "FinishedAt" = {now} WHERE "Id" = {attempt.Id}
            """, token);
        if (job.StatusCode == "running")
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE "EmailSyncJobs" SET "StatusCode" = 'dispatch-unknown', "ErrorCode" = 'EmailDispatchUnknown' WHERE "Id" = {id}
                """, token);
        await tx.CommitAsync(token);
    }

    public async Task<IntegrationReception> ReceiveAsync(IntegrationAccess access, string json, CancellationToken token)
    {
        var result = EmailSyncContracts.ParseResult(json); var canonical = EmailSyncContracts.Serialize(result); var hash = Hash(canonical);
        var id = await db.EmailSyncJobs.AsNoTracking().Where(x => x.IntegrationOperationId == result.OperationId
            && x.WorkspaceId == access.WorkspaceId && x.ConnectionId == access.ConnectionId).Select(x => (Guid?)x.Id).SingleOrDefaultAsync(token)
            ?? throw new IntegrationAccessException();
        await using var tx = await db.Database.BeginTransactionAsync(token);
        var (_, job, op) = await EmailSyncLocks.JobAsync(db, access.WorkspaceId, id, token);
        if (op.ConnectionId != access.ConnectionId) throw new IntegrationAccessException();
        var existing = await AuthorizedReceiptAsync(access, result.MessageId, token);
        if (existing is not null) return Duplicate(existing, canonical, hash);
        Match(job, op, result);
        // Failed/cancelled dispatches may keep a late trace, but never reopen or import.
        var terminal = job.StatusCode is "cancelled" or "failed";
        if ((!terminal && op.StatusCode != "awaiting-result") || job.StartedAt is null
            || await db.IntegrationReceipts.AnyAsync(x => x.OperationId == op.Id, token)) throw new EmailSyncException("EmailResultNotExpected");
        var now = await EmailSyncLocks.Now(db, token); DateTimeOffset? processed = terminal ? now : null;
        var inserted = await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "IntegrationReceipts" ("MessageId","OperationId","CanonicalContent","ContentSha256","ReceivedAt","ProcessedAt")
            VALUES ({result.MessageId},{op.Id},{canonical},{hash},{now},{processed}) ON CONFLICT ("MessageId") DO NOTHING
            """, token);
        if (inserted == 0) return Duplicate(await AuthorizedReceiptAsync(access, result.MessageId, token)
            ?? throw new EmailSyncException("EmailReceiptConflict"), canonical, hash);
        if (!terminal)
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE "IntegrationOperations" SET "StatusCode" = 'result-received', "Revision" = "Revision" + 1 WHERE "Id" = {op.Id};
                UPDATE "EmailSyncJobs" SET "StatusCode" = 'result-received', "NextCursor" = {result.Payload.NextCursor}, "ErrorCode" = NULL WHERE "Id" = {id}
                """, token);
        await tx.CommitAsync(token); return IntegrationReception.Recorded;
    }

    public async Task ProcessAsync(Guid workspace, Guid id, CancellationToken token)
    {
        await using var tx = await db.Database.BeginTransactionAsync(token);
        await EmailSyncLocks.WorkspaceAsync(db, workspace, token);
        var (account, job, op) = await EmailSyncLocks.JobAsync(db, workspace, id, token);
        var receipt = await EmailSyncLocks.ReceiptAsync(db, op.Id, token);
        // Lost HTTP response after commit: an exact second process is a successful no-op.
        if (job.StatusCode is "succeeded" or "failed" or "cancelled" && receipt?.ProcessedAt is not null) return;
        if (job.StatusCode != "result-received" || op.StatusCode != "result-received" || receipt is null || receipt.ProcessedAt is not null)
            throw new EmailSyncException("EmailResultNotReady");
        var state = await EmailSyncLocks.StateAsync(db, account.Id, token);
        var error = ContextError(account, job, state);
        EmailSyncResult? result = null;
        try
        {
            result = EmailSyncContracts.ParseResult(receipt.CanonicalContent); Match(job, op, result);
            if (receipt.ContentSha256 != Hash(receipt.CanonicalContent) || result.MessageId != receipt.MessageId || result.Payload.NextCursor != job.NextCursor)
                error = "EmailResultInvalid";
        }
        catch (Exception e) when (e is IntegrationContractException or EmailSyncException) { error = "EmailResultInvalid"; }
        if (error is null && result!.Payload.Outcome == "failed") error = "EmailProviderFailed";
        var imported = error is null ? await importer.ImportAsync(account, result!.Payload.Messages, token) : new EmailImportResult(0, 0, null);
        if (imported.ConflictId is not null) error = "EmailMessageContentConflict";
        await FinishAsync(job, op, receipt, error, imported, token);
        if (error is null)
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE "EmailSyncStates" SET "Cursor" = {job.NextCursor}, "Revision" = "Revision" + 1,
                    "LastSuccessfulSyncAt" = statement_timestamp(), "UpdatedAt" = statement_timestamp() WHERE "EmailAccountId" = {account.Id}
                """, token);
        token.ThrowIfCancellationRequested();
        await tx.CommitAsync(token);
        if (error is not null && error != "EmailProviderFailed") throw new EmailSyncException(error);
    }

    public async Task CancelAsync(Guid workspace, Guid id, CancellationToken token)
    {
        await using var tx = await db.Database.BeginTransactionAsync(token);
        var (_, job, op) = await EmailSyncLocks.JobAsync(db, workspace, id, token);
        if (job.StatusCode == "cancelled") return;
        if (job.FinishedAt is not null) throw new EmailSyncException("EmailSyncAlreadyFinished");
        var receipt = await EmailSyncLocks.ReceiptAsync(db, op.Id, token);
        await FinishAsync(job, op, receipt, "EmailSyncCancelled", token: token);
        await tx.CommitAsync(token);
    }

    private async Task FinishAsync(EmailSyncJob job, IntegrationOperation op, IntegrationReceipt? receipt, string? error,
        EmailImportResult? imported = null, CancellationToken token = default)
    {
        var status = error is null ? "succeeded" : "failed"; var jobStatus = error == "EmailSyncCancelled" ? "cancelled" : status;
        var now = await EmailSyncLocks.Now(db, token);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "EmailSyncJobs" SET "StatusCode" = {jobStatus}, "FinishedAt" = {now}, "ErrorCode" = {error},
                "ConflictingExternalMessageId" = {(imported == null ? null : imported.ConflictId)},
                "CreatedCount" = {(imported == null ? 0 : imported.Created)}, "ExistingCount" = {(imported == null ? 0 : imported.Existing)} WHERE "Id" = {job.Id};
            UPDATE "IntegrationOperations" SET "StatusCode" = {status}, "CompletedAt" = {now}, "Revision" = "Revision" + 1 WHERE "Id" = {op.Id}
            """, token);
        if (receipt is not null)
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE \"IntegrationReceipts\" SET \"ProcessedAt\" = {now} WHERE \"MessageId\" = {receipt.MessageId}", token);
    }
    private string? ContextError(EmailAccount account, EmailSyncJob job, EmailSyncState state)
    {
        if (account.StatusCode != "active" || account.ArchivedAt is not null) return "EmailAccountUnavailable";
        if (account.Revision != job.AccountRevision) return "EmailAccountChanged";
        try { accounts.RequireConnection(job.WorkspaceId, job.ConnectionId); }
        catch (EmailSyncException) { return "EmailConnectionMismatch"; }
        return state.Revision != job.StateRevision || state.Cursor != job.PreviousCursor ? "EmailCursorConflict" : null;
    }
    private static void RequireActive(EmailAccount account)
    { if (account.StatusCode != "active" || account.ArchivedAt is not null) throw new EmailSyncException("EmailAccountUnavailable"); }
    private static void Match(EmailSyncJob job, IntegrationOperation op, EmailSyncResult result)
    {
        if (result.OperationId != op.Id || result.CommandMessageId != op.CommandMessageId || result.CorrelationId != op.CorrelationId
            || result.Version != op.Version || result.Type != op.Type || result.Payload.AccountRef != job.EmailAccountId || result.Payload.Messages.Count > job.MaxItems)
            throw new EmailSyncException("EmailResultContextMismatch");
    }
    private async Task<IntegrationReceipt?> AuthorizedReceiptAsync(IntegrationAccess access, Guid id, CancellationToken token)
    {
        var receipt = await db.IntegrationReceipts.AsNoTracking().SingleOrDefaultAsync(x => x.MessageId == id, token);
        if (receipt is not null && !await db.IntegrationOperations.AnyAsync(x => x.Id == receipt.OperationId
            && x.WorkspaceId == access.WorkspaceId && x.ConnectionId == access.ConnectionId, token)) throw new IntegrationAccessException();
        return receipt;
    }
    private static IntegrationReception Duplicate(IntegrationReceipt receipt, string canonical, string hash)
    {
        if (receipt.CanonicalContent != canonical || receipt.ContentSha256 != hash) throw new EmailSyncException("EmailReceiptContentConflict");
        return IntegrationReception.AlreadyRecorded;
    }
    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static EmailSyncJobDto Dto(EmailSyncJob x) => new(x.Id, x.EmailAccountId, x.IntegrationOperationId, x.TriggerTypeCode, x.StatusCode,
        x.EnqueuedAt, x.StartedAt, x.FinishedAt, x.ErrorCode, x.ConflictingExternalMessageId, x.CreatedCount, x.ExistingCount);
}
