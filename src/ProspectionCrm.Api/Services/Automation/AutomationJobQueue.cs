using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using ProspectionCrm.Api.Data;
using ProspectionCrm.Api.Dtos.AutomationJobs;
using ProspectionCrm.Api.Entities;

namespace ProspectionCrm.Api.Services.Automation;

public sealed class AutomationJobQueue(ProspectionCrmDbContext db, IOptions<AutomationJobQueueOptions> options)
    : IAutomationJobQueue
{
    public async Task<AutomationJobResult<AutomationJob>> EnqueueAsync(Guid workspaceId,
        EnqueueAutomationJobRequest request, CancellationToken token)
    {
        var error = Validate(request);
        if (error is not null) return new(null, 400, error);
        if (request.AutomationRuleId is { } ruleId && !await db.AutomationRules.AsNoTracking()
            .AnyAsync(x => x.Id == ruleId && x.WorkspaceId == workspaceId, token))
            return new(null, 404, "AutomationRuleNotFound");

        var id = Guid.NewGuid();
        var priority = request.Priority ?? AutomationJobLimits.DefaultPriority;
        var available = request.AvailableAt?.ToUniversalTime();
        int inserted;
        try
        {
            // The partial unique index arbitrates concurrent inserts. The subsequent read has a new
            // READ COMMITTED snapshot, including the winner if ON CONFLICT waited for its commit.
            inserted = await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "AutomationJobs" ("Id", "WorkspaceId", "AutomationRuleId", "TriggerTypeCode", "TriggerKey",
                    "ActionCategoryCode", "StatusCode", "Priority", "AvailableAt", "AttemptCount", "ContextJson", "CreatedAt")
                VALUES ({id}, {workspaceId}, {request.AutomationRuleId}, {request.TriggerTypeCode}, {request.TriggerKey},
                    {request.ActionCategoryCode}, {AutomationJobStatuses.Pending}, {priority},
                    COALESCE({available}, statement_timestamp()), 0, CAST({request.ContextJson} AS jsonb), statement_timestamp())
                ON CONFLICT ("WorkspaceId", "TriggerTypeCode", "TriggerKey") WHERE "TriggerKey" IS NOT NULL DO NOTHING
                """, token);
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.ForeignKeyViolation)
        { return new(null, 409, "ConcurrentAutomationJobChange"); }
        catch (PostgresException exception) when (exception.SqlState is "22P02" or "22P05" or "22021")
        { return new(null, 400, "InvalidContextJson"); }

        var query = db.AutomationJobs.AsNoTracking().Where(x => x.WorkspaceId == workspaceId);
        var job = inserted == 1 ? await query.SingleAsync(x => x.Id == id, token)
            : await query.SingleAsync(x => x.TriggerTypeCode == request.TriggerTypeCode && x.TriggerKey == request.TriggerKey, token);
        return new(job, inserted == 1 ? 201 : 200);
    }

    private static string? Validate(EnqueueAutomationJobRequest request)
    {
        if (!AutomationJobTriggers.IsValid(request.TriggerTypeCode)) return "InvalidTriggerTypeCode";
        if (!AutomationCategories.IsValid(request.ActionCategoryCode)) return "InvalidActionCategoryCode";
        if (request.Priority is < AutomationJobLimits.MinPriority or > AutomationJobLimits.MaxPriority) return "InvalidPriority";
        if (request.TriggerKey is { } key && (string.IsNullOrWhiteSpace(key)
            || key.Length > AutomationJobLimits.TriggerKeyLength || key.Any(char.IsControl))) return "InvalidTriggerKey";
        if (request.AvailableAt is { } available && (available == DateTimeOffset.MinValue || available == DateTimeOffset.MaxValue))
            return "InvalidAvailableAt";
        if (request.ContextJson is not { } json) return null;
        if (Encoding.UTF8.GetByteCount(json) > AutomationJobLimits.ContextBytes) return "InvalidContextJson";
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return "InvalidContextJson";
        }
        catch (JsonException) { return "InvalidContextJson"; }
        return null;
    }

    public async Task<AutomationJob?> ClaimAsync(Guid workspaceId, CancellationToken token)
    {
        var duration = options.Value.LeaseDuration;
        if (duration <= TimeSpan.Zero || duration > TimeSpan.FromHours(1))
            throw new InvalidOperationException("Automation job lease duration must be positive and at most one hour.");
        // A fresh capability per claim fences out a previous attempt, including when the same worker returns.
        var owner = Guid.NewGuid().ToString("N");
        var rows = await db.AutomationJobs.FromSqlInterpolated($"""
            WITH candidate AS (
                SELECT "Id" FROM "AutomationJobs"
                WHERE "WorkspaceId" = {workspaceId} AND "StatusCode" = {AutomationJobStatuses.Pending}
                    AND "AvailableAt" <= statement_timestamp()
                ORDER BY "Priority" DESC, "AvailableAt", "CreatedAt", "Id"
                LIMIT 1 FOR UPDATE SKIP LOCKED
            )
            UPDATE "AutomationJobs" j SET "StatusCode" = {AutomationJobStatuses.Leased},
                "LeaseOwner" = {owner}, "LeaseExpiresAt" = statement_timestamp() + {duration},
                "AttemptCount" = j."AttemptCount" + 1, "UpdatedAt" = statement_timestamp()
            FROM candidate c WHERE j."Id" = c."Id" RETURNING j.*
            """).AsNoTracking().ToListAsync(token);
        return rows.SingleOrDefault();
    }

    public Task<bool> CompleteAsync(Guid workspaceId, Guid jobId, string leaseOwner, CancellationToken token)
        => FinishAsync(workspaceId, jobId, leaseOwner, AutomationJobStatuses.Completed, null, null, token);

    public Task<bool> FailAsync(Guid workspaceId, Guid jobId, string leaseOwner, string? error, CancellationToken token)
        => FinishAsync(workspaceId, jobId, leaseOwner, AutomationJobStatuses.Failed, AutomationJobErrors.Sanitize(error), null, token);

    public Task<bool> ReleaseAsync(Guid workspaceId, Guid jobId, string leaseOwner, DateTimeOffset? availableAt, CancellationToken token)
        => FinishAsync(workspaceId, jobId, leaseOwner, AutomationJobStatuses.Pending, null, availableAt?.ToUniversalTime(), token);

    private async Task<bool> FinishAsync(Guid workspaceId, Guid jobId, string leaseOwner, string status,
        string? error, DateTimeOffset? availableAt, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(leaseOwner)) return false;
        // Materialize the lock before checking expiry: a wait for another transaction cannot make
        // a previously evaluated expiry predicate authorize a now-expired lease.
        return await db.Database.ExecuteSqlInterpolatedAsync($"""
            WITH owned AS MATERIALIZED (
                SELECT "Id", "LeaseExpiresAt" FROM "AutomationJobs" WHERE "Id" = {jobId} AND "WorkspaceId" = {workspaceId}
                    AND "StatusCode" = {AutomationJobStatuses.Leased} AND "LeaseOwner" = {leaseOwner} FOR UPDATE
            )
            UPDATE "AutomationJobs" j SET "StatusCode" = {status}, "LeaseOwner" = NULL, "LeaseExpiresAt" = NULL,
                "UpdatedAt" = clock_timestamp(), "LastError" = {error},
                "CompletedAt" = CASE WHEN {status} = {AutomationJobStatuses.Pending} THEN NULL ELSE clock_timestamp() END,
                "AvailableAt" = CASE WHEN {status} = {AutomationJobStatuses.Pending}
                    THEN COALESCE({availableAt}, clock_timestamp()) ELSE j."AvailableAt" END
            FROM owned o WHERE j."Id" = o."Id" AND o."LeaseExpiresAt" > clock_timestamp()
            """, token) == 1;
    }

    public async Task<bool> CancelAsync(Guid workspaceId, Guid jobId, CancellationToken token)
    {
        var changed = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "AutomationJobs" SET "StatusCode" = {AutomationJobStatuses.Cancelled},
                "CompletedAt" = statement_timestamp(), "UpdatedAt" = statement_timestamp()
            WHERE "Id" = {jobId} AND "WorkspaceId" = {workspaceId} AND "StatusCode" = {AutomationJobStatuses.Pending}
            """, token);
        return changed == 1 || await db.AutomationJobs.AsNoTracking().AnyAsync(x => x.Id == jobId
            && x.WorkspaceId == workspaceId && x.StatusCode == AutomationJobStatuses.Cancelled, token);
    }

    public Task<int> RecoverAsync(Guid workspaceId, CancellationToken token) => db.Database.ExecuteSqlInterpolatedAsync($"""
        WITH expired AS (
            SELECT "Id" FROM "AutomationJobs" WHERE "WorkspaceId" = {workspaceId}
                AND "StatusCode" = {AutomationJobStatuses.Leased} AND "LeaseExpiresAt" <= statement_timestamp()
            ORDER BY "LeaseExpiresAt", "Id" LIMIT {AutomationJobLimits.RecoveryBatchSize} FOR UPDATE SKIP LOCKED
        )
        UPDATE "AutomationJobs" j SET "StatusCode" = {AutomationJobStatuses.Pending}, "LeaseOwner" = NULL,
            "LeaseExpiresAt" = NULL, "UpdatedAt" = statement_timestamp()
        FROM expired e WHERE j."Id" = e."Id"
        """, token);
}
