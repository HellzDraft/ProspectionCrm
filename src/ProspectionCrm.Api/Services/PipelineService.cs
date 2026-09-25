using Microsoft.EntityFrameworkCore;
using ProspectionCrm.Api.Data;
using ProspectionCrm.Api.Dtos.Pipelines;

namespace ProspectionCrm.Api.Services;

public class PipelineService(ProspectionCrmDbContext dbContext, ICurrentWorkspaceProvider currentWorkspaceProvider) : IPipelineService
{
    public async Task<IReadOnlyList<PipelineDto>> GetAllAsync(bool includeArchived = false, CancellationToken cancellationToken = default)
    {
        var workspaceId = await currentWorkspaceProvider.GetCurrentWorkspaceIdAsync(cancellationToken);
        var pipelines = await dbContext.Pipelines.AsNoTracking()
            .Where(x => x.WorkspaceId == workspaceId && (includeArchived || x.ArchivedAt == null))
            .Include(x => x.Stages.Where(stage => includeArchived || stage.ArchivedAt == null))
            .OrderBy(x => x.Name).ThenBy(x => x.Id)
            .ToListAsync(cancellationToken);
        return pipelines.Select(pipeline => new PipelineDto
        {
            Id = pipeline.Id,
            Name = pipeline.Name,
            Description = pipeline.Description,
            TypeCode = pipeline.TypeCode,
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
        }).ToList();
    }
}
