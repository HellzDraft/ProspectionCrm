using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using ProspectionCrm.Api.Data;
using ProspectionCrm.Api.Data.Configurations;
using ProspectionCrm.Api.Dtos.CollectionJobs;
using ProspectionCrm.Api.Entities;

namespace ProspectionCrm.Api.Services.Collection;

public sealed class SourceCollectionJobService(ProspectionCrmDbContext db, ICurrentWorkspaceProvider workspaceProvider,
    ISourceCollectionContextResolver resolver) : ISourceCollectionJobService
{
    private static readonly Expression<Func<SourceCollectionJob, SourceCollectionJobDto>> Projection = x => new(
        x.Id, x.WorkspaceId, x.SavedSearchId, x.PipelineId, x.PipelineStageId, x.TriggerTypeCode, x.StatusCode,
        x.EnqueuedAt, x.AvailableAt, x.StartedAt, x.FinishedAt, x.AttemptCount, x.SourceExecutionId, x.ErrorCode);

    public async Task<SourceCollectionJobResult<SourceCollectionJobDto>> EnqueueAsync(Guid savedSearchId,
        EnqueueSourceCollectionJobRequest request, CancellationToken token)
    {
        if (request.PipelineStageId is null || request.PipelineStageId == Guid.Empty)
            return new(SourceCollectionJobStatus.InvalidRequest, Code: "InvalidRequest");
        var workspaceId = await WorkspaceAsync(token);
        if (workspaceId is null) return Unavailable<SourceCollectionJobDto>();
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        try
        {
            var resolved = await resolver.ResolveAsync(workspaceId.Value, savedSearchId, request.PipelineStageId.Value, token);
            if (resolved.Error is { } error)
                return new(error.StatusCode == 404 ? SourceCollectionJobStatus.NotFound :
                    error.StatusCode == 400 ? SourceCollectionJobStatus.InvalidRequest : SourceCollectionJobStatus.Conflict, Code: error.Code);
            var context = resolved.Value!;
            var now = DateTimeOffset.UtcNow;
            var job = new SourceCollectionJob
            {
                WorkspaceId = workspaceId.Value, SavedSearchId = savedSearchId, PipelineId = context.Pipeline.Id,
                PipelineStageId = context.Stage.Id, TriggerTypeCode = "manual", StatusCode = "queued",
                EnqueuedAt = now, AvailableAt = now, AttemptCount = 0
            };
            db.SourceCollectionJobs.Add(job);
            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
            return new(SourceCollectionJobStatus.Succeeded, Projection.Compile()(job));
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException
            { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: SourceCollectionJobConfiguration.ActiveIndex })
        {
            await transaction.RollbackAsync(token);
            db.ChangeTracker.Clear();
            var existing = await db.SourceCollectionJobs.AsNoTracking().Where(x => x.WorkspaceId == workspaceId
                && x.SavedSearchId == savedSearchId && x.PipelineStageId == request.PipelineStageId
                && (x.StatusCode == "queued" || x.StatusCode == "running")).Select(x => (Guid?)x.Id).SingleOrDefaultAsync(token);
            return new(SourceCollectionJobStatus.Conflict, Code: "CollectionJobAlreadyPending", ExistingJobId: existing);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException
            { SqlState: PostgresErrorCodes.ForeignKeyViolation or PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.DeadlockDetected })
        {
            await transaction.RollbackAsync(token);
            db.ChangeTracker.Clear();
            return new(SourceCollectionJobStatus.Conflict, Code: "ConcurrentCollectionJobChange");
        }
    }

    public async Task<SourceCollectionJobResult<SourceCollectionJobDto>> GetAsync(Guid id, CancellationToken token)
    {
        var workspaceId = await WorkspaceAsync(token);
        if (workspaceId is null) return Unavailable<SourceCollectionJobDto>();
        var job = await db.SourceCollectionJobs.AsNoTracking().Where(x => x.Id == id && x.WorkspaceId == workspaceId)
            .Select(Projection).SingleOrDefaultAsync(token);
        return job is null ? new(SourceCollectionJobStatus.NotFound, Code: "ResourceNotFound") : new(SourceCollectionJobStatus.Succeeded, job);
    }

    public async Task<SourceCollectionJobResult<SourceCollectionJobsPageDto>> ListAsync(int offset, int limit,
        string? statusCode, Guid? savedSearchId, string? triggerTypeCode, CancellationToken token)
    {
        if (offset < 0 || limit is < 1 or > 200) return new(SourceCollectionJobStatus.InvalidRequest, Code: "InvalidPagination");
        if (statusCode is not (null or "queued" or "running" or "succeeded" or "failed" or "cancelled"))
            return new(SourceCollectionJobStatus.InvalidRequest, Code: "InvalidStatusCode");
        if (triggerTypeCode is not (null or "manual" or "scheduled" or "event" or "retry"))
            return new(SourceCollectionJobStatus.InvalidRequest, Code: "InvalidTriggerTypeCode");
        var workspaceId = await WorkspaceAsync(token);
        if (workspaceId is null) return Unavailable<SourceCollectionJobsPageDto>();
        var query = db.SourceCollectionJobs.AsNoTracking().Where(x => x.WorkspaceId == workspaceId);
        if (statusCode is not null) query = query.Where(x => x.StatusCode == statusCode);
        if (savedSearchId is not null) query = query.Where(x => x.SavedSearchId == savedSearchId);
        if (triggerTypeCode is not null) query = query.Where(x => x.TriggerTypeCode == triggerTypeCode);
        var total = await query.CountAsync(token);
        var items = await query.OrderByDescending(x => x.EnqueuedAt).ThenByDescending(x => x.Id)
            .Skip(offset).Take(limit).Select(Projection).ToListAsync(token);
        return new(SourceCollectionJobStatus.Succeeded, new(offset, limit, total, (long)offset + items.Count < total, items));
    }

    public async Task<SourceCollectionJobResult<bool>> CancelAsync(Guid id, CancellationToken token)
    {
        var workspaceId = await WorkspaceAsync(token);
        if (workspaceId is null) return Unavailable<bool>();
        var query = db.SourceCollectionJobs.Where(x => x.Id == id && x.WorkspaceId == workspaceId);
        var now = DateTimeOffset.UtcNow;
        // Conditional UPDATE locks and rechecks the current row, including after a concurrent cancellation/claim.
        var changed = await query.Where(x => x.StatusCode == "queued").ExecuteUpdateAsync(s =>
            s.SetProperty(x => x.StatusCode, "cancelled").SetProperty(x => x.FinishedAt, now), token);
        if (changed == 1) return new(SourceCollectionJobStatus.Succeeded, true);
        var status = await query.AsNoTracking().Select(x => x.StatusCode).SingleOrDefaultAsync(token);
        return status switch
        {
            null => new(SourceCollectionJobStatus.NotFound, Code: "ResourceNotFound"),
            "cancelled" => new(SourceCollectionJobStatus.Succeeded, true),
            "running" or "succeeded" or "failed" => new(SourceCollectionJobStatus.Conflict, Code: "CollectionJobNotCancellable"),
            _ => new(SourceCollectionJobStatus.Conflict, Code: "ConcurrentCollectionJobChange")
        };
    }

    private async Task<Guid?> WorkspaceAsync(CancellationToken token)
    {
        try { return await workspaceProvider.GetCurrentWorkspaceIdAsync(token); }
        catch (InvalidOperationException) { return null; }
    }
    private static SourceCollectionJobResult<T> Unavailable<T>() => new(SourceCollectionJobStatus.Conflict, Code: "WorkspaceUnavailable");
}
