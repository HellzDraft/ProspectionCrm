using Microsoft.AspNetCore.Mvc;
using ProspectionCrm.Api.Dtos.Pipelines;
using ProspectionCrm.Api.Services;

namespace ProspectionCrm.Api.Controllers;

[ApiController]
[Route("api/pipelines")]
public class PipelinesController(IPipelineService pipelineService) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<PipelineDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<PipelineDto>>> GetAll(
        CancellationToken cancellationToken, [FromQuery] bool includeArchived = false)
        => Ok(await pipelineService.GetAllAsync(includeArchived, cancellationToken));
}
