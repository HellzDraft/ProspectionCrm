using Microsoft.AspNetCore.Mvc;
using ProspectionCrm.Api.Dtos.SourceExecutions;
using ProspectionCrm.Api.Services;

namespace ProspectionCrm.Api.Controllers;

[ApiController]
[Route("api/source-executions")]
public class SourceExecutionsController(ISourceExecutionService service) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<SourceExecutionDto>>> GetAll(CancellationToken cancellationToken,
        [FromQuery] Guid? sourceConfigurationId = null, [FromQuery] Guid? savedSearchId = null,
        [FromQuery] string? statusCode = null)
        => Ok(await service.GetAllAsync(sourceConfigurationId, savedSearchId, statusCode, cancellationToken));

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<SourceExecutionDto>> GetById(Guid id, CancellationToken cancellationToken)
    {
        var execution = await service.GetByIdAsync(id, cancellationToken);
        return execution is null ? NotFound() : Ok(execution);
    }
}
