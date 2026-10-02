using ProspectionCrm.Api.Dtos.Pipelines;

namespace ProspectionCrm.Api.Dtos.Setup;

public sealed record InitialPipelinesDto(
    IReadOnlyList<PipelineDto> Pipelines,
    IReadOnlyList<Guid> CreatedPipelineIds);
