using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using ProspectionCrm.Api.Data;
using ProspectionCrm.Api.Dtos.Integrations;
using ProspectionCrm.Api.Entities;
using ProspectionCrm.Api.Services.Integrations;

namespace ProspectionCrm.Api.Services.Collection;

// All multi-row transitions lock job -> operation. No network and no business ingestion here.
public sealed class RemoteCollectionState(ProspectionCrmDbContext db, ISourceCollectionContextResolver resolver)
{
    public async Task<RemoteCollectionIntent> PrepareAsync(SourceCollectionJob claimed, Guid connection, CancellationToken token)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var job = await LockJobAsync(claimed.WorkspaceId, claimed.Id, token);
        Own(job, claimed);
        var context = await ContextAsync(job, token);
        var fingerprint = CollectionFingerprint.Create(context.Search, context.Source, context.Pipeline, context.Stage);
        var existing = await db.RemoteCollectionIntents.AsNoTracking().SingleOrDefaultAsync(x => x.JobId == job.Id, token);
        if (existing is not null)
        {
            if (existing.ConnectionId != connection) throw Conflict("RemoteConnectionMismatch");
            if (existing.Fingerprint != fingerprint) throw Conflict("RemoteContextChanged");
            await transaction.CommitAsync(token);
            return existing;
        }
        var now = await Now(token);
        var op = new IntegrationOperation { Id = Guid.NewGuid(), WorkspaceId = job.WorkspaceId, ConnectionId = connection,
            CommandMessageId = Guid.NewGuid(), CorrelationId = Guid.NewGuid(), ProbeId = Guid.Empty,
            Version = 1, Type = CollectionIntegrationContracts.Type, StatusCode = "pending", CreatedAt = now };
        var command = new CollectionCommandEnvelope(1, op.CommandMessageId, op.Id, op.CorrelationId, op.Type,
            CollectionIntegrationContracts.Criteria(context.Search.CriteriaJson, Guid.NewGuid()));
        var intent = new RemoteCollectionIntent { JobId = job.Id, WorkspaceId = job.WorkspaceId, ConnectionId = connection,
            OperationId = op.Id, OperationType = op.Type, SourceConfigurationId = context.Source.Id, CreatedAt = now,
            Fingerprint = fingerprint, ContextSnapshotJson = IngestionHistory.Context(context.Source, context.Search, context.Pipeline, context.Stage),
            CommandJson = CollectionIntegrationContracts.Serialize(command) };
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "IntegrationOperations"
                ("Id","WorkspaceId","ConnectionId","CommandMessageId","CorrelationId","ProbeId","Version","Type","StatusCode","Revision","CreatedAt")
            VALUES ({op.Id},{op.WorkspaceId},{op.ConnectionId},{op.CommandMessageId},{op.CorrelationId},{op.ProbeId},1,{op.Type},'pending',0,{now});
            INSERT INTO "RemoteCollectionIntents"
                ("JobId","WorkspaceId","OperationId","ConnectionId","OperationType","SourceConfigurationId","Fingerprint","ContextSnapshotJson","CommandJson","CreatedAt")
            VALUES ({intent.JobId},{intent.WorkspaceId},{intent.OperationId},{intent.ConnectionId},{intent.OperationType},
                {intent.SourceConfigurationId},{intent.Fingerprint},{intent.ContextSnapshotJson},{intent.CommandJson},{now});
            """, token);
        await transaction.CommitAsync(token);
        return intent;
    }

    public async Task<IntegrationAttempt> BeginAsync(SourceCollectionJob claimed, RemoteCollectionIntent intent, CancellationToken token)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var job = await LockJobAsync(claimed.WorkspaceId, claimed.Id, token);
        Own(job, claimed);
        if (intent.JobId != job.Id || intent.WorkspaceId != job.WorkspaceId
            || !await db.RemoteCollectionIntents.AnyAsync(x => x.JobId == job.Id && x.OperationId == intent.OperationId
                && x.ConnectionId == intent.ConnectionId && x.WorkspaceId == job.WorkspaceId, token))
            throw new IntegrationAccessException();
        var op = await LockOperationAsync(intent, token);
        var last = await LastAttemptAsync(op.Id, token);
        // Only a definite rejection permits retransmission of the SAME persisted command.
        if (op.StatusCode is not ("pending" or "awaiting-result") || last is not null && last.StatusCode != "failed")
            throw Conflict("RemoteDispatchUnknown");
        var attempt = new IntegrationAttempt { Id = Guid.NewGuid(), OperationId = op.Id, Number = (last?.Number ?? 0) + 1,
            StatusCode = "started", StartedAt = await Now(token) };
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "IntegrationAttempts" ("Id","OperationId","Number","StatusCode","StartedAt")
            VALUES ({attempt.Id},{op.Id},{attempt.Number},'started',{attempt.StartedAt});
            UPDATE "IntegrationOperations" SET "StatusCode" = 'awaiting-result', "Revision" = "Revision" + 1 WHERE "Id" = {op.Id};
            """, token);
        await transaction.CommitAsync(token);
        return attempt;
    }

    // Invoked by the existing finalizer with the job row lock and exclusive worker guard held.
    // Recovery uses exactly the same durable evidence, never the absence of a SourceExecution.
    public async Task<bool> FinishAsync(SourceCollectionJob job, string? errorCode, SourceCollectionRetryPolicy policy, CancellationToken token)
    {
        if (db.Database.CurrentTransaction is null) throw new InvalidOperationException("Remote finalization requires a transaction.");
        var intent = await db.RemoteCollectionIntents.AsNoTracking().SingleOrDefaultAsync(x => x.JobId == job.Id, token);
        if (intent is null) return false;
        var op = await LockOperationAsync(intent, token);
        var last = await LastAttemptAsync(op.Id, token);
        if (op.StatusCode == "result-received") { await ParkAsync(job, "result-received", token); return true; }
        if (last?.StatusCode == "acknowledged") { await ParkAsync(job, "awaiting-result", token); return true; }
        if (last?.StatusCode is "started" or "unknown")
        {
            if (last.StatusCode == "started")
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    UPDATE "IntegrationAttempts" SET "StatusCode" = 'unknown', "FinishedAt" = GREATEST(statement_timestamp(), "StartedAt")
                    WHERE "Id" = {last.Id} AND "StatusCode" = 'started'
                    """, token);
            await ParkAsync(job, "dispatch-unknown", token);
            return true;
        }
        var code = errorCode is null or "CollectionWorkerError" or "CollectionWorkerStopping" or "CollectionLeaseExpired"
            ? last is null ? "RemoteDispatchNotStarted" : "RemoteDispatchFailed"
            : SourceCollectionRetryPolicy.Normalize(errorCode);
        var retry = policy.CanRetry(job.AttemptCount, code, null);
        var status = retry ? "queued" : "failed";
        var delay = policy.Backoff(job.AttemptCount);
        string? terminalError = retry ? null : code;
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "SourceCollectionJobAttempts" SET "StatusCode" = 'failed', "ErrorCode" = {code},
                "FinishedAt" = GREATEST(statement_timestamp(), "StartedAt")
                WHERE "JobId" = {job.Id} AND "AttemptNumber" = {job.AttemptCount};
            UPDATE "SourceCollectionJobs" SET "StatusCode" = {status}, "ErrorCode" = {terminalError},
                "AvailableAt" = CASE WHEN {retry} THEN GREATEST(statement_timestamp(), "AvailableAt") + {delay} ELSE "AvailableAt" END,
                "StartedAt" = CASE WHEN {retry} THEN NULL ELSE "StartedAt" END,
                "FinishedAt" = CASE WHEN {retry} THEN NULL ELSE GREATEST(statement_timestamp(), "StartedAt") END,
                "LeaseToken" = NULL, "LeaseExpiresAt" = NULL WHERE "Id" = {job.Id};
            """, token);
        return true;
    }

    public async Task<IntegrationReception> ReceiveAsync(IntegrationAccess access, string json, CancellationToken token)
    {
        var result = CollectionIntegrationContracts.ParseResult(json);
        var canonical = CollectionIntegrationContracts.Serialize(result);
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
        // Scope the link before acquiring locks; authorization is repeated under the operation lock.
        var intent = await db.RemoteCollectionIntents.AsNoTracking().SingleOrDefaultAsync(x => x.OperationId == result.OperationId
            && x.WorkspaceId == access.WorkspaceId && x.ConnectionId == access.ConnectionId, token) ?? throw new IntegrationAccessException();
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var job = await LockJobAsync(access.WorkspaceId, intent.JobId, token);
        var op = await LockOperationAsync(intent, token);
        if (op.WorkspaceId != access.WorkspaceId || op.ConnectionId != access.ConnectionId) throw new IntegrationAccessException();
        var existing = await AuthorizedReceiptAsync(access, result.MessageId, token);
        if (existing is not null) return Duplicate(existing, canonical, hash);
        var command = CollectionIntegrationContracts.ParseCommand(intent.CommandJson);
        if (result.OperationId != op.Id || result.CorrelationId != op.CorrelationId || result.CommandMessageId != op.CommandMessageId
            || result.Version != op.Version || result.Type != op.Type || result.Payload.RequestId != command.Payload.RequestId
            || result.Payload.Items.Count > command.Payload.MaxItems) throw Conflict("operation-context-conflict");
        if (op.StatusCode != "awaiting-result" || job.StatusCode == "queued") throw Conflict("operation-state-conflict");
        // Cancellation/terminal failure accepts a trace, but can never reopen the job.
        if (job.StatusCode is not ("cancelled" or "failed"))
        {
            var context = await ContextAsync(job, token);
            if (intent.Fingerprint != CollectionFingerprint.Create(context.Search, context.Source, context.Pipeline, context.Stage))
                throw Conflict("RemoteContextChanged");
        }
        var inserted = await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "IntegrationReceipts" ("MessageId","OperationId","CanonicalContent","ContentSha256","ReceivedAt")
            VALUES ({result.MessageId},{op.Id},{canonical},{hash},GREATEST(statement_timestamp(), {op.CreatedAt}))
            ON CONFLICT ("MessageId") DO NOTHING
            """, token);
        if (inserted == 0)
            return Duplicate(await AuthorizedReceiptAsync(access, result.MessageId, token) ?? throw Conflict("receipt-concurrency-conflict"), canonical, hash);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "IntegrationOperations" SET "StatusCode" = 'result-received', "Revision" = "Revision" + 1 WHERE "Id" = {op.Id}
            """, token);
        if (job.StatusCode is "running" or "awaiting-result" or "dispatch-unknown") await ParkAsync(job, "result-received", token);
        await transaction.CommitAsync(token);
        return IntegrationReception.Recorded;
    }

    private async Task ParkAsync(SourceCollectionJob job, string status, CancellationToken token)
    {
        string? code = status == "dispatch-unknown" ? "RemoteDispatchUnknown" : null;
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "SourceCollectionJobAttempts" SET "StatusCode" = {status}, "ErrorCode" = {code}, "FinishedAt" = NULL
                WHERE "JobId" = {job.Id} AND "AttemptNumber" = {job.AttemptCount};
            UPDATE "SourceCollectionJobs" SET "StatusCode" = {status}, "ErrorCode" = {code}, "FinishedAt" = NULL,
                "LeaseToken" = NULL, "LeaseExpiresAt" = NULL WHERE "Id" = {job.Id};
            """, token);
    }

    // A callback can clear the lease before HTTP finalization. After a crash that job no longer
    // belongs to the expired-lease scan, but its transport attempt still needs a truthful outcome.
    public async Task<int> RecoverDetachedAttemptsAsync(CancellationToken token)
    {
        if (db.Database.CurrentTransaction is null) throw new InvalidOperationException("Recovery requires a transaction.");
        var jobs = await db.SourceCollectionJobs.FromSqlRaw("""
            SELECT j.* FROM "SourceCollectionJobs" j
            WHERE j."StatusCode" IN ('awaiting-result','dispatch-unknown','result-received','cancelled','failed')
                AND EXISTS (SELECT 1 FROM "RemoteCollectionIntents" i JOIN "IntegrationAttempts" a ON a."OperationId" = i."OperationId"
                    WHERE i."JobId" = j."Id" AND a."StatusCode" = 'started')
            ORDER BY j."Id" LIMIT 8 FOR UPDATE OF j SKIP LOCKED
            """).AsNoTracking().ToListAsync(token);
        var count = 0;
        foreach (var job in jobs)
        {
            var key = SourceCollectionJobGuard.Key(job.Id);
            if (!await db.Database.SqlQuery<bool>($"SELECT pg_try_advisory_xact_lock(hashtextextended({key}, 0)) AS \"Value\"").SingleAsync(token)) continue;
            var intent = await db.RemoteCollectionIntents.AsNoTracking().SingleAsync(x => x.JobId == job.Id, token);
            _ = await LockOperationAsync(intent, token);
            count += await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE "IntegrationAttempts" SET "StatusCode" = 'unknown', "FinishedAt" = GREATEST(statement_timestamp(), "StartedAt")
                    WHERE "OperationId" = {intent.OperationId} AND "StatusCode" = 'started'
                """, token);
        }
        return count;
    }

    private async Task<SourceCollectionContext> ContextAsync(SourceCollectionJob job, CancellationToken token)
    {
        // Consistent snapshot with resource mutation excluded until this short transaction commits.
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            SELECT 1 FROM "Workspaces" WHERE "Id" = {job.WorkspaceId} FOR SHARE;
            SELECT 1 FROM "SavedSearches" WHERE "Id" = {job.SavedSearchId} FOR SHARE;
            SELECT 1 FROM "SourceConfigurations" WHERE "Id" = (SELECT "SourceConfigurationId" FROM "SavedSearches" WHERE "Id" = {job.SavedSearchId}) FOR SHARE;
            SELECT 1 FROM "Pipelines" WHERE "Id" = {job.PipelineId} FOR SHARE;
            SELECT 1 FROM "PipelineStages" WHERE "Id" = {job.PipelineStageId} FOR SHARE;
            """, token);
        var context = await resolver.ResolveAsync(job.WorkspaceId, job.SavedSearchId, job.PipelineStageId, token);
        if (context.Value is not { IsRemote: true } value || value.Pipeline.Id != job.PipelineId) throw Conflict("RemoteContextChanged");
        return value;
    }
    private async Task<SourceCollectionJob> LockJobAsync(Guid workspace, Guid id, CancellationToken token) =>
        await db.SourceCollectionJobs.FromSqlInterpolated($"""
            SELECT * FROM "SourceCollectionJobs" WHERE "Id" = {id} AND "WorkspaceId" = {workspace} FOR UPDATE
            """).AsNoTracking().SingleAsync(token);
    private async Task<IntegrationOperation> LockOperationAsync(RemoteCollectionIntent intent, CancellationToken token) =>
        await db.IntegrationOperations.FromSqlInterpolated($"""
            SELECT * FROM "IntegrationOperations" WHERE "Id" = {intent.OperationId} AND "WorkspaceId" = {intent.WorkspaceId}
                AND "ConnectionId" = {intent.ConnectionId} AND "Type" = {CollectionIntegrationContracts.Type} FOR UPDATE
            """).AsNoTracking().SingleOrDefaultAsync(token) ?? throw new IntegrationAccessException();
    private Task<IntegrationAttempt?> LastAttemptAsync(Guid op, CancellationToken token) => db.IntegrationAttempts.AsNoTracking()
        .Where(x => x.OperationId == op).OrderByDescending(x => x.Number).FirstOrDefaultAsync(token);
    private static void Own(SourceCollectionJob job, SourceCollectionJob claimed)
    {
        if (job.StatusCode != "running" || job.LeaseToken != claimed.LeaseToken || job.AttemptCount != claimed.AttemptCount)
            throw Conflict("collection-job-ownership-lost");
    }
    private async Task<IntegrationReceipt?> AuthorizedReceiptAsync(IntegrationAccess access, Guid message, CancellationToken token)
    {
        var receipt = await db.IntegrationReceipts.AsNoTracking().SingleOrDefaultAsync(x => x.MessageId == message, token);
        if (receipt is not null && !await db.IntegrationOperations.AnyAsync(x => x.Id == receipt.OperationId
            && x.WorkspaceId == access.WorkspaceId && x.ConnectionId == access.ConnectionId, token)) throw new IntegrationAccessException();
        return receipt;
    }
    private static IntegrationReception Duplicate(IntegrationReceipt receipt, string canonical, string hash)
    {
        if (receipt.CanonicalContent != canonical || receipt.ContentSha256 != hash) throw Conflict("message-content-conflict");
        return IntegrationReception.AlreadyRecorded;
    }
    private Task<DateTimeOffset> Now(CancellationToken token) => db.Database.SqlQuery<DateTimeOffset>($"SELECT statement_timestamp() AS \"Value\"").SingleAsync(token);
    private static IntegrationConflictException Conflict(string code) => new(code);
}
