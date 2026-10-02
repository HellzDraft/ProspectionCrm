using ProspectionCrm.Api.Dtos.Pipelines;

namespace ProspectionCrm.Api.Services;

public interface IInitialPipelineService
{
    Task<InitialPipelineResult> InitializeAsync(CancellationToken cancellationToken);
}

public sealed record InitialPipelineResult(PipelineDto? Pipeline = null, bool Created = false, string? Error = null);
