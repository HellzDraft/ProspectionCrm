using Microsoft.EntityFrameworkCore;
using ProspectionCrm.Api.Data;

namespace ProspectionCrm.Api.Services;

internal static class RulePipelineValidation
{
    internal static async Task<string?> ValidateAsync(ProspectionCrmDbContext dbContext,
        Guid workspaceId, Guid? pipelineId, bool requireActive, CancellationToken cancellationToken)
    {
        if (!pipelineId.HasValue)
            return null;

        var pipeline = await dbContext.Pipelines.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == pipelineId && x.WorkspaceId == workspaceId, cancellationToken);
        if (pipeline is null)
            return "PipelineId must reference a pipeline in the current workspace.";
        return requireActive && pipeline.ArchivedAt.HasValue
            ? "PipelineId must reference an active pipeline." : null;
    }
}
