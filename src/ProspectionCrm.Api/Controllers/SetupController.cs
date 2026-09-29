using Microsoft.AspNetCore.Mvc;
using ProspectionCrm.Api.Dtos.Setup;
using ProspectionCrm.Api.Services;

namespace ProspectionCrm.Api.Controllers;

[ApiController]
[Route("api/setup")]
public class SetupController(IBootstrapService bootstrapService) : ControllerBase
{
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
