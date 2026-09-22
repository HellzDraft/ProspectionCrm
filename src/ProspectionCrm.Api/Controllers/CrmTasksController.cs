using Microsoft.AspNetCore.Mvc;
using ProspectionCrm.Api.Dtos.CrmTasks;
using ProspectionCrm.Api.Services;

namespace ProspectionCrm.Api.Controllers;

[ApiController]
[Route("api/crm-tasks")]
public class CrmTasksController(ICrmTaskService crmTaskService) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<CrmTaskDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<CrmTaskDto>>> GetAll(
        [FromQuery] Guid? opportunityId, [FromQuery] bool? isCompleted, CancellationToken cancellationToken)
        => Ok(await crmTaskService.GetAllAsync(opportunityId, isCompleted, cancellationToken));

    [HttpGet("due")]
    [ProducesResponseType<IReadOnlyList<CrmTaskDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<CrmTaskDto>>> GetDue(CancellationToken cancellationToken)
        => Ok(await crmTaskService.GetDueAsync(cancellationToken));

    [HttpGet("{id:guid}")]
    [ProducesResponseType<CrmTaskDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<CrmTaskDto>> GetById(Guid id, CancellationToken cancellationToken)
    {
        var crmTask = await crmTaskService.GetByIdAsync(id, cancellationToken);
        return crmTask is null ? NotFound() : Ok(crmTask);
    }

    [HttpPost]
    [ProducesResponseType<CrmTaskDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<CrmTaskDto>> Create(
        CreateCrmTaskRequest request, CancellationToken cancellationToken)
    {
        var (crmTask, error) = await crmTaskService.CreateAsync(request, cancellationToken);
        if (error is not null)
            return Problem(detail: error, statusCode: StatusCodes.Status400BadRequest);

        return CreatedAtAction(nameof(GetById), new { id = crmTask!.Id }, crmTask);
    }

    [HttpPut("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Update(
        Guid id, UpdateCrmTaskRequest request, CancellationToken cancellationToken)
    {
        var (found, error) = await crmTaskService.UpdateAsync(id, request, cancellationToken);
        if (!found)
            return NotFound();
        if (error is not null)
            return Problem(detail: error, statusCode: StatusCodes.Status400BadRequest);

        return NoContent();
    }

    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
        => await crmTaskService.DeleteAsync(id, cancellationToken) ? NoContent() : NotFound();

    [HttpPost("{id:guid}/complete")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Complete(Guid id, CancellationToken cancellationToken)
        => await crmTaskService.CompleteAsync(id, cancellationToken) ? NoContent() : NotFound();

    [HttpPost("{id:guid}/reopen")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Reopen(Guid id, CancellationToken cancellationToken)
        => await crmTaskService.ReopenAsync(id, cancellationToken) ? NoContent() : NotFound();
}
