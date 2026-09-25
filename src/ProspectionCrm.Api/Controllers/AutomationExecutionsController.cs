using Microsoft.AspNetCore.Mvc;
using ProspectionCrm.Api.Dtos.AutomationExecutions;
using ProspectionCrm.Api.Services;

namespace ProspectionCrm.Api.Controllers;

[ApiController]
[Route("api/automation-executions")]
public class AutomationExecutionsController(IAutomationExecutionService service) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<AutomationExecutionDto>>> GetAll(CancellationToken cancellationToken,
        [FromQuery] Guid? automationRuleId = null, [FromQuery] string? statusCode = null, [FromQuery] DateTimeOffset? from = null, [FromQuery] DateTimeOffset? to = null)
    {
        var (items, error) = await service.GetAllAsync(automationRuleId, statusCode, from, to, cancellationToken);
        return error is null ? Ok(items) : Problem(detail: error, statusCode: StatusCodes.Status400BadRequest);
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<AutomationExecutionDto>> GetById(Guid id, CancellationToken cancellationToken)
    {
        var item = await service.GetByIdAsync(id, cancellationToken);
        return item is null ? NotFound() : Ok(item);
    }
}
