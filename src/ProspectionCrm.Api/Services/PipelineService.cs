using Microsoft.EntityFrameworkCore;
using ProspectionCrm.Api.Data;
using ProspectionCrm.Api.Dtos.Pipelines;
using ProspectionCrm.Api.Entities;

namespace ProspectionCrm.Api.Services;

public class PipelineService(ProspectionCrmDbContext dbContext, ICurrentWorkspaceProvider currentWorkspaceProvider) : IPipelineService
{
    public async Task<IReadOnlyList<PipelineDto>> GetAllAsync(bool includeArchived = false, CancellationToken cancellationToken = default)
    {
        var workspaceId = await currentWorkspaceProvider.GetCurrentWorkspaceIdAsync(cancellationToken);
        var pipelines = await ReadQuery(workspaceId, includeArchived)
            .Where(x => includeArchived || x.ArchivedAt == null)
            .OrderBy(x => x.Name).ThenBy(x => x.Id)
            .ToListAsync(cancellationToken);
        return pipelines.Select(ToDto).ToList();
    }

    public async Task<PipelineDto?> GetByIdAsync(Guid id, bool includeArchivedStages = false, CancellationToken cancellationToken = default)
    {
        var workspaceId = await currentWorkspaceProvider.GetCurrentWorkspaceIdAsync(cancellationToken);
        // Like other detail endpoints, archived resources remain addressable by their ID.
        var pipeline = await ReadQuery(workspaceId, includeArchivedStages)
            .SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        return pipeline is null ? null : ToDto(pipeline);
    }

    private IQueryable<Pipeline> ReadQuery(Guid workspaceId, bool includeArchived)
        => dbContext.Pipelines.AsNoTracking()
            .Where(x => x.WorkspaceId == workspaceId)
            .Include(x => x.Workspace)
            .Include(x => x.Stages.Where(stage => includeArchived || stage.ArchivedAt == null));

    public async Task<(PipelineDto? Pipeline, string? Error)> CreateAsync(PipelineWriteRequest request, CancellationToken cancellationToken)
    {
        var workspaceId = await currentWorkspaceProvider.GetCurrentWorkspaceIdAsync(cancellationToken);
        var error = Validate(request);
        if (error is not null)
            return (null, error);
        var pipeline = new Pipeline
        {
            WorkspaceId = workspaceId,
            Name = request.Name.Trim(),
            Description = request.Description,
            TypeCode = request.TypeCode,
            IsVisible = request.IsVisible
        };
        dbContext.Pipelines.Add(pipeline);
        await dbContext.SaveChangesAsync(cancellationToken);
        return (ToDto(pipeline), null);
    }

    public async Task<(bool Found, string? Error)> UpdateAsync(Guid id, PipelineWriteRequest request, CancellationToken cancellationToken)
    {
        var workspaceId = await currentWorkspaceProvider.GetCurrentWorkspaceIdAsync(cancellationToken);
        var pipeline = await dbContext.Pipelines.SingleOrDefaultAsync(
            x => x.Id == id && x.WorkspaceId == workspaceId, cancellationToken);
        if (pipeline is null)
            return (false, null);
        var error = Validate(request);
        if (error is not null)
            return (true, error);
        pipeline.Name = request.Name.Trim();
        pipeline.Description = request.Description;
        pipeline.TypeCode = request.TypeCode;
        pipeline.IsVisible = request.IsVisible;
        pipeline.UpdatedAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
        return (true, null);
    }

    public Task<bool> ArchiveAsync(Guid id, CancellationToken cancellationToken)
        => SetArchivedAsync(id, archive: true, cancellationToken);

    public Task<bool> RestoreAsync(Guid id, CancellationToken cancellationToken)
        => SetArchivedAsync(id, archive: false, cancellationToken);

    private async Task<bool> SetArchivedAsync(Guid id, bool archive, CancellationToken cancellationToken)
    {
        var workspaceId = await currentWorkspaceProvider.GetCurrentWorkspaceIdAsync(cancellationToken);
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var workspace = await LockWorkspaceAsync(workspaceId, cancellationToken);
        var pipeline = await dbContext.Pipelines.SingleOrDefaultAsync(
            x => x.Id == id && x.WorkspaceId == workspaceId, cancellationToken);
        if (pipeline is null)
            return false;

        if (archive && workspace.DefaultPipelineId == id)
        {
            workspace.DefaultPipelineId = null;
            workspace.UpdatedAt = DateTimeOffset.UtcNow;
        }
        if (archive != (pipeline.ArchivedAt is not null))
        {
            pipeline.ArchivedAt = archive ? DateTimeOffset.UtcNow : null;
            pipeline.UpdatedAt = DateTimeOffset.UtcNow;
        }
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<(bool Found, string? Error)> SetDefaultAsync(Guid id, CancellationToken cancellationToken)
    {
        var workspaceId = await currentWorkspaceProvider.GetCurrentWorkspaceIdAsync(cancellationToken);
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var workspace = await LockWorkspaceAsync(workspaceId, cancellationToken);
        var pipeline = await dbContext.Pipelines.SingleOrDefaultAsync(
            x => x.Id == id && x.WorkspaceId == workspaceId, cancellationToken);
        if (pipeline is null)
            return (false, null);
        if (pipeline.ArchivedAt is not null)
            return (true, "An archived pipeline cannot be the default pipeline.");
        if (workspace.DefaultPipelineId != id)
        {
            workspace.DefaultPipelineId = id;
            workspace.UpdatedAt = DateTimeOffset.UtcNow;
        }
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return (true, null);
    }

    // Lock before reading the pipeline. All lifecycle/default operations follow this order,
    // so concurrent archive/set-default calls cannot leave an archived default behind.
    // The composite FK separately enforces same-workspace ownership, even for direct SQL.
    private Task<Workspace> LockWorkspaceAsync(Guid workspaceId, CancellationToken cancellationToken)
        => dbContext.Workspaces.FromSqlInterpolated(
            $"SELECT * FROM \"Workspaces\" WHERE \"Id\" = {workspaceId} FOR UPDATE")
            .SingleAsync(cancellationToken);

    private static string? Validate(PipelineWriteRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 200)
            return "Name is required and must not exceed 200 characters after trimming.";
        if (request.Description?.Length > 2000)
            return "Description must not exceed 2000 characters.";
        if (request.TypeCode is not ("employment" or "freelance" or "business" or "custom"))
            return "TypeCode must be employment, freelance, business or custom.";
        return null;
    }

    private static PipelineDto ToDto(Pipeline pipeline) => new()
    {
        Id = pipeline.Id,
        Name = pipeline.Name,
        Description = pipeline.Description,
        TypeCode = pipeline.TypeCode,
        IsVisible = pipeline.IsVisible,
        IsDefault = pipeline.Workspace?.DefaultPipelineId == pipeline.Id,
        PreferredCandidateProfileId = pipeline.PreferredCandidateProfileId,
        CreatedAt = pipeline.CreatedAt,
        UpdatedAt = pipeline.UpdatedAt,
        ArchivedAt = pipeline.ArchivedAt,
        Stages = pipeline.Stages.OrderBy(stage => stage.SortOrder).ThenBy(stage => stage.Id)
            .Select(stage => new PipelineStageDto
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
            }).ToList()
    };
}
