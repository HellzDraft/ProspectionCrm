using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ProspectionCrm.Api.Data;

namespace ProspectionCrm.Api.Services.Collection;

public interface ISourceCollectionSchedulingService
{
    Task<int> ScheduleDueAsync(CancellationToken token);
}

public sealed class SourceCollectionSchedulingService(ProspectionCrmDbContext db,
    IOptions<SourceCollectionSchedulerOptions> options, ILogger<SourceCollectionSchedulingService> logger)
    : ISourceCollectionSchedulingService
{
    public async Task<int> ScheduleDueAsync(CancellationToken token)
    {
        var visited = new List<Guid>();
        var processed = 0;
        for (var i = 0; i < options.Value.BatchSize; i++)
        {
            token.ThrowIfCancellationRequested();
            Guid? searchId = null;
            try
            {
                // One short transaction per search: an isolated error cannot roll back other occurrences.
                await using var tx = await db.Database.BeginTransactionAsync(token);
                var excluded = visited.ToArray();
                var search = (await db.SavedSearches.FromSqlInterpolated($"""
                    SELECT s.* FROM "SavedSearches" s
                    JOIN "SourceCollectionSchedules" c ON c."SavedSearchId" = s."Id"
                        AND c."WorkspaceId" = s."WorkspaceId" AND c."PipelineId" = s."PipelineId"
                    WHERE c."Enabled" AND c."NextCollectionAt" <= statement_timestamp()
                        AND NOT (s."Id" = ANY({excluded}))
                    ORDER BY c."NextCollectionAt", s."Id" LIMIT 1 FOR NO KEY UPDATE OF s SKIP LOCKED
                    """).AsNoTracking().ToListAsync(token)).SingleOrDefault();
                if (search is null) break;
                searchId = search.Id;
                visited.Add(search.Id);
                var schedule = await db.SourceCollectionSchedules.SingleAsync(x => x.SavedSearchId == search.Id
                    && x.WorkspaceId == search.WorkspaceId, token);
                var now = await db.Database.SqlQuery<DateTimeOffset>($"SELECT statement_timestamp() AS \"Value\"").SingleAsync(token);
                // Recheck after locking; a concurrent configuration may have committed before acquisition.
                if (!schedule.Enabled || schedule.NextCollectionAt > now) continue;
                if (!await SourceCollectionScheduleService.IsTargetValidAsync(db, search, schedule.PipelineStageId, token))
                {
                    schedule.Enabled = false;
                    schedule.NextCollectionAt = null;
                    logger.LogWarning("Collection schedule suspended for search {SavedSearchId} in workspace {WorkspaceId}: inactive target",
                        search.Id, search.WorkspaceId);
                }
                else
                {
                    var jobId = Guid.NewGuid();
                    // Infer only the existing partial unique index; a manual enqueue can race this INSERT.
                    var inserted = await db.Database.ExecuteSqlInterpolatedAsync($"""
                        INSERT INTO "SourceCollectionJobs" ("Id", "WorkspaceId", "SavedSearchId", "PipelineId", "PipelineStageId",
                            "TriggerTypeCode", "StatusCode", "EnqueuedAt", "AvailableAt", "AttemptCount")
                        VALUES ({jobId}, {search.WorkspaceId}, {search.Id}, {search.PipelineId}, {schedule.PipelineStageId},
                            'scheduled', 'queued', {now}, {now}, 0)
                        ON CONFLICT ("WorkspaceId", "SavedSearchId", "PipelineStageId")
                            WHERE "StatusCode" IN ('queued', 'running') DO NOTHING
                        """, token);
                    // Compute after INSERT in case uniqueness arbitration had to wait for another transaction.
                    var advancedAt = await db.Database.SqlQuery<DateTimeOffset>($"SELECT statement_timestamp() AS \"Value\"").SingleAsync(token);
                    schedule.NextCollectionAt = SourceCollectionScheduleCalculator.Next(advancedAt, schedule.DailyUtcMinute);
                    if (inserted == 0)
                        logger.LogInformation("Collection occurrence coalesced for search {SavedSearchId} in workspace {WorkspaceId}: active job exists",
                            search.Id, search.WorkspaceId);
                }
                await db.SaveChangesAsync(token);
                await tx.CommitAsync(token);
                processed++;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                logger.LogError(exception, "Collection scheduling failed for search {SavedSearchId}; other searches remain eligible", searchId);
                // A database-wide error before selecting a row should wait for the next poll.
                if (searchId is null) break;
            }
            finally { db.ChangeTracker.Clear(); }
        }
        return processed;
    }
}
