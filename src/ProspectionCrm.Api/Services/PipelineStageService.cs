using System.Data;
using Microsoft.EntityFrameworkCore;
using ProspectionCrm.Api.Data;
using ProspectionCrm.Api.Dtos.Pipelines;
using ProspectionCrm.Api.Entities;

namespace ProspectionCrm.Api.Services;

public class PipelineStageService(ProspectionCrmDbContext dbContext, ICurrentWorkspaceProvider currentWorkspaceProvider) : IPipelineStageService
{
    public async Task<IReadOnlyList<PipelineStageDto>?> GetAllAsync(
        Guid pipelineId, bool includeArchived, CancellationToken cancellationToken)
    {
        var workspaceId = await currentWorkspaceProvider.GetCurrentWorkspaceIdAsync(cancellationToken);
        if (!await dbContext.Pipelines.AnyAsync(x => x.Id == pipelineId && x.WorkspaceId == workspaceId, cancellationToken))
            return null;
        var stages = await dbContext.PipelineStages.AsNoTracking()
            .Where(x => x.PipelineId == pipelineId && (includeArchived || x.ArchivedAt == null))
            .OrderBy(x => x.SortOrder).ThenBy(x => x.Id).ToListAsync(cancellationToken);
        return stages.Select(ToDto).ToList();
    }

    public async Task<PipelineStageDto?> GetByIdAsync(Guid pipelineId, Guid stageId, CancellationToken cancellationToken)
    {
        var workspaceId = await currentWorkspaceProvider.GetCurrentWorkspaceIdAsync(cancellationToken);
        var stage = await dbContext.PipelineStages.AsNoTracking().SingleOrDefaultAsync(
            x => x.Id == stageId && x.PipelineId == pipelineId && x.Pipeline.WorkspaceId == workspaceId, cancellationToken);
        return stage is null ? null : ToDto(stage);
    }

    public async Task<PipelineStageWriteResult> CreateAsync(
        Guid pipelineId, PipelineStageWriteRequest request, CancellationToken cancellationToken)
    {
        var workspaceId = await currentWorkspaceProvider.GetCurrentWorkspaceIdAsync(cancellationToken);
        await using var transaction = await dbContext.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var pipeline = await LockPipelineAsync(workspaceId, pipelineId, cancellationToken);
        if (pipeline is null)
            return new(PipelineStageWriteStatus.NotFound);
        if (pipeline.ArchivedAt is not null)
            return ParentArchived();
        var error = Validate(request);
        if (error is not null)
            return new(PipelineStageWriteStatus.InvalidInput, Error: error);

        // Archived stages still occupy their positions. Never fill gaps or renumber existing stages.
        var lastOrder = await dbContext.PipelineStages.Where(x => x.PipelineId == pipelineId)
            .MaxAsync(x => (int?)x.SortOrder, cancellationToken);
        if (lastOrder == int.MaxValue)
            return new(PipelineStageWriteStatus.Conflict, Error: "No further SortOrder is available in this pipeline.");
        var stage = new PipelineStage
        {
            PipelineId = pipelineId,
            Name = request.Name.Trim(),
            Description = request.Description,
            CategoryCode = request.CategoryCode,
            SortOrder = lastOrder.HasValue ? lastOrder.Value + 1 : 0
        };
        dbContext.PipelineStages.Add(stage);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(PipelineStageWriteStatus.Succeeded, ToDto(stage));
    }

    public async Task<PipelineStageWriteResult> UpdateAsync(
        Guid pipelineId, Guid stageId, PipelineStageWriteRequest request, CancellationToken cancellationToken)
    {
        var workspaceId = await currentWorkspaceProvider.GetCurrentWorkspaceIdAsync(cancellationToken);
        await using var transaction = await dbContext.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var pipeline = await LockPipelineAsync(workspaceId, pipelineId, cancellationToken);
        if (pipeline is null)
            return new(PipelineStageWriteStatus.NotFound);
        var stage = await dbContext.PipelineStages.SingleOrDefaultAsync(
            x => x.Id == stageId && x.PipelineId == pipelineId, cancellationToken);
        if (stage is null)
            return new(PipelineStageWriteStatus.NotFound);
        if (pipeline.ArchivedAt is not null)
            return ParentArchived();
        if (stage.ArchivedAt is not null)
            return new(PipelineStageWriteStatus.Conflict, Error: "Restore the archived stage before modifying it.");
        var error = Validate(request);
        if (error is not null)
            return new(PipelineStageWriteStatus.InvalidInput, Error: error);

        stage.Name = request.Name.Trim();
        stage.Description = request.Description;
        stage.CategoryCode = request.CategoryCode;
        stage.UpdatedAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(PipelineStageWriteStatus.Succeeded);
    }

    public Task<PipelineStageWriteResult> ArchiveAsync(Guid pipelineId, Guid stageId, CancellationToken cancellationToken)
        => SetArchivedAsync(pipelineId, stageId, archive: true, cancellationToken);

    public Task<PipelineStageWriteResult> RestoreAsync(Guid pipelineId, Guid stageId, CancellationToken cancellationToken)
        => SetArchivedAsync(pipelineId, stageId, archive: false, cancellationToken);

    private async Task<PipelineStageWriteResult> SetArchivedAsync(
        Guid pipelineId, Guid stageId, bool archive, CancellationToken cancellationToken)
    {
        var workspaceId = await currentWorkspaceProvider.GetCurrentWorkspaceIdAsync(cancellationToken);
        await using var transaction = await dbContext.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var pipeline = await LockPipelineAsync(workspaceId, pipelineId, cancellationToken);
        if (pipeline is null)
            return new(PipelineStageWriteStatus.NotFound);
        var stage = await dbContext.PipelineStages.SingleOrDefaultAsync(
            x => x.Id == stageId && x.PipelineId == pipelineId, cancellationToken);
        if (stage is null)
            return new(PipelineStageWriteStatus.NotFound);
        if (pipeline.ArchivedAt is not null)
            return ParentArchived();
        if (archive != (stage.ArchivedAt is not null))
        {
            stage.ArchivedAt = archive ? DateTimeOffset.UtcNow : null;
            stage.UpdatedAt = DateTimeOffset.UtcNow;
        }
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(PipelineStageWriteStatus.Succeeded);
    }

    // The parent row serializes stage writes and conflicts with PipelineService's archive UPDATE.
    // ReadCommitted sees the preceding commit after waiting. No workspace lock is acquired here,
    // so this cannot invert PipelineService's workspace-then-pipeline lock order.
    private Task<Pipeline?> LockPipelineAsync(Guid workspaceId, Guid pipelineId, CancellationToken cancellationToken)
        => dbContext.Pipelines.FromSqlInterpolated(
            $"SELECT * FROM \"Pipelines\" WHERE \"Id\" = {pipelineId} AND \"WorkspaceId\" = {workspaceId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);

    private static PipelineStageWriteResult ParentArchived()
        => new(PipelineStageWriteStatus.Conflict, Error: "Restore the archived pipeline before changing its stages.");

    private static string? Validate(PipelineStageWriteRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 200)
            return "Name is required and must not exceed 200 characters after trimming.";
        if (request.Description?.Length > 2000)
            return "Description must not exceed 2000 characters.";
        if (request.CategoryCode is not ("active" or "success" or "failure"))
            return "CategoryCode must be active, success or failure.";
        return null;
    }

    private static PipelineStageDto ToDto(PipelineStage stage) => new()
    {
        Id = stage.Id,
        PipelineId = stage.PipelineId,
        Name = stage.Name,
        Description = stage.Description,
        SortOrder = stage.SortOrder,
        CategoryCode = stage.CategoryCode,
        CreatedAt = stage.CreatedAt,
        UpdatedAt = stage.UpdatedAt,
        ArchivedAt = stage.ArchivedAt
    };
}
