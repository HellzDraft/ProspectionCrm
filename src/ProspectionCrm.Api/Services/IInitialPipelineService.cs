using ProspectionCrm.Api.Dtos.Setup;

namespace ProspectionCrm.Api.Services;

public interface IInitialPipelineService
{
    Task<InitialPipelineResult> InitializeAsync(CancellationToken cancellationToken);
}

public sealed record InitialPipelineResult(InitialPipelinesDto? Result = null, string? Error = null);
