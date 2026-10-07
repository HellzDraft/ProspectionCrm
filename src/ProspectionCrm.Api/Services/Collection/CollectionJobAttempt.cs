using Microsoft.EntityFrameworkCore;
using ProspectionCrm.Api.Data;

namespace ProspectionCrm.Api.Services.Collection;

// Internal orchestration metadata, never accepted from an HTTP body.
public sealed record CollectionJobAttempt(Guid JobId, Guid LeaseToken, Guid ExecutionId, string TriggerTypeCode)
{
    public async Task AttachExecutionAsync(ProspectionCrmDbContext db, Guid workspaceId, CancellationToken token)
    {
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Attaching collection history requires its transaction.");
        var changed = await db.SourceCollectionJobs.Where(x => x.Id == JobId && x.WorkspaceId == workspaceId
            && x.StatusCode == "running" && x.LeaseToken == LeaseToken && x.SourceExecutionId == null)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.SourceExecutionId, ExecutionId), token);
        if (changed != 1) throw new InvalidOperationException("Collection job ownership was lost.");
    }
}
