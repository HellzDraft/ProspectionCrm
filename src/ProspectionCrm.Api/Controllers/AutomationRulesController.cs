using Microsoft.AspNetCore.Mvc;
using ProspectionCrm.Api.Dtos.AutomationRules;
using ProspectionCrm.Api.Services;

namespace ProspectionCrm.Api.Controllers;

[ApiController]
[Route("api/automation-rules")]
public class AutomationRulesController(IAutomationRuleService service) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<AutomationRuleDto>>> GetAll(CancellationToken cancellationToken,
        [FromQuery] bool includeArchived = false, [FromQuery] Guid? pipelineId = null, [FromQuery] bool? enabled = null, [FromQuery] string? triggerTypeCode = null)
        => Ok(await service.GetAllAsync(includeArchived, pipelineId, enabled, triggerTypeCode, cancellationToken));

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<AutomationRuleDto>> GetById(Guid id, CancellationToken cancellationToken)
    {
        var item = await service.GetByIdAsync(id, cancellationToken);
        return item is null ? NotFound() : Ok(item);
    }

    [HttpPost]
    public async Task<ActionResult<AutomationRuleDto>> Create(CreateAutomationRuleRequest request, CancellationToken cancellationToken)
    {
        var (item, error) = await service.CreateAsync(request, cancellationToken);
        if (error is not null)
            return Problem(detail: error, statusCode: StatusCodes.Status400BadRequest);
        return CreatedAtAction(nameof(GetById), new { id = item!.Id }, item);
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, UpdateAutomationRuleRequest request, CancellationToken cancellationToken)
    {
        var (found, error) = await service.UpdateAsync(id, request, cancellationToken);
        if (!found)
            return NotFound();
        return error is null ? NoContent() : Problem(detail: error, statusCode: StatusCodes.Status400BadRequest);
    }

    [HttpPost("{id:guid}/archive")]
    public async Task<IActionResult> Archive(Guid id, CancellationToken cancellationToken)
        => await service.ArchiveAsync(id, cancellationToken) ? NoContent() : NotFound();

    [HttpPost("{id:guid}/restore")]
    public async Task<IActionResult> Restore(Guid id, CancellationToken cancellationToken)
    {
        var (found, error) = await service.RestoreAsync(id, cancellationToken);
        if (!found)
            return NotFound();
        return error is null ? NoContent() : Problem(detail: error, statusCode: StatusCodes.Status400BadRequest);
    }
}
