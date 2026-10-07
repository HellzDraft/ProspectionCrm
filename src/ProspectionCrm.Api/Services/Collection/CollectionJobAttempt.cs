using Microsoft.EntityFrameworkCore;
using ProspectionCrm.Api.Data;

namespace ProspectionCrm.Api.Services.Collection;

// Internal orchestration metadata, never accepted from an HTTP body.
public sealed record CollectionJobAttempt(Guid JobId, Guid LeaseToken, Guid ExecutionId, string TriggerTypeCode)
{
    public async Task AttachExecutionAsync(ProspectionCrmDbContext db, Guid workspaceId, CancellationToken token, int? upstreamStatus = null, string? errorCode = null)
    {
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Attaching collection history requires its transaction.");
        var changed = await db.SourceCollectionJobs.Where(x => x.Id == JobId && x.WorkspaceId == workspaceId
            && x.StatusCode == "running" && x.LeaseToken == LeaseToken && x.SourceExecutionId == null)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.SourceExecutionId, ExecutionId), token);
        if (changed != 1) throw new InvalidOperationException("Collection job ownership was lost.");
        var number = await db.SourceCollectionJobs.Where(x => x.Id == JobId).Select(x => x.AttemptCount).SingleAsync(token);
        changed = await db.SourceCollectionJobAttempts.Where(x => x.JobId == JobId && x.WorkspaceId == workspaceId
            && x.AttemptNumber == number && x.StatusCode == "running" && x.SourceExecutionId == null)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.SourceExecutionId, ExecutionId)
                .SetProperty(x => x.UpstreamStatusCode, upstreamStatus)
                .SetProperty(x => x.ErrorCode, errorCode == null ? null : SourceCollectionRetryPolicy.Normalize(errorCode)), token);
        if (changed != 1) throw new InvalidOperationException("Collection attempt history is inconsistent.");
    }
}
