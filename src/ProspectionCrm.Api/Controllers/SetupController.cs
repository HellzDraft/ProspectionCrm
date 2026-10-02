using Microsoft.AspNetCore.Mvc;
using ProspectionCrm.Api.Dtos.Setup;
using ProspectionCrm.Api.Dtos.Pipelines;
using ProspectionCrm.Api.Services;

namespace ProspectionCrm.Api.Controllers;

[ApiController]
[Route("api/setup")]
public class SetupController(IBootstrapService bootstrapService, IInitialPipelineService initialPipelineService) : ControllerBase
{
    [HttpPost("initial-pipelines")]
    [ProducesResponseType<PipelineDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<PipelineDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<PipelineDto>> InitializePipelines(CancellationToken cancellationToken)
    {
        var result = await initialPipelineService.InitializeAsync(cancellationToken);
        if (result.Error is not null)
            return Problem(statusCode: StatusCodes.Status409Conflict, title: "Initial pipeline setup conflict", detail: result.Error);
        return result.Created
            ? CreatedAtAction(nameof(PipelinesController.GetById), "Pipelines", new { id = result.Pipeline!.Id }, result.Pipeline)
            : Ok(result.Pipeline);
    }

    [HttpPost("bootstrap")]
    [ProducesResponseType<BootstrapDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<BootstrapDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<BootstrapDto>> Bootstrap(CancellationToken cancellationToken)
    {
        var (bootstrap, created, error) = await bootstrapService.BootstrapAsync(cancellationToken);
        if (error is not null)
            return Problem(statusCode: StatusCodes.Status409Conflict, title: "Incompatible V1 setup state", detail: error);

        return created ? StatusCode(StatusCodes.Status201Created, bootstrap) : Ok(bootstrap);
    }
}
