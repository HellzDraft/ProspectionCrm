using ProspectionCrm.Api.Dtos.Pipelines;

namespace ProspectionCrm.Api.Services;

public interface IPipelineStageService
{
    Task<IReadOnlyList<PipelineStageDto>?> GetAllAsync(Guid pipelineId, bool includeArchived, CancellationToken cancellationToken);
    Task<PipelineStageDto?> GetByIdAsync(Guid pipelineId, Guid stageId, CancellationToken cancellationToken);
    Task<PipelineStageWriteResult> CreateAsync(Guid pipelineId, PipelineStageWriteRequest request, CancellationToken cancellationToken);
    Task<PipelineStageWriteResult> UpdateAsync(Guid pipelineId, Guid stageId, PipelineStageWriteRequest request, CancellationToken cancellationToken);
    Task<PipelineStageWriteResult> ArchiveAsync(Guid pipelineId, Guid stageId, CancellationToken cancellationToken);
    Task<PipelineStageWriteResult> RestoreAsync(Guid pipelineId, Guid stageId, CancellationToken cancellationToken);
}

// Local write outcome: distinguishes input validation from a conflicting lifecycle state.
public enum PipelineStageWriteStatus { Succeeded, NotFound, InvalidInput, Conflict }

public sealed record PipelineStageWriteResult(
    PipelineStageWriteStatus Status, PipelineStageDto? Stage = null, string? Error = null);
