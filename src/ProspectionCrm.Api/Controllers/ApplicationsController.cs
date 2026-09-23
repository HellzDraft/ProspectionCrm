using Microsoft.AspNetCore.Mvc;
using ProspectionCrm.Api.Dtos.Applications;
using ProspectionCrm.Api.Services;

namespace ProspectionCrm.Api.Controllers;

[ApiController]
[Route("api/opportunities/{opportunityId:guid}/applications")]
public class ApplicationsController(IApplicationService service) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<ApplicationDto>>> GetAll(Guid opportunityId, CancellationToken cancellationToken)
    {
        var entities = await service.GetAllAsync(opportunityId, cancellationToken);
        return entities is null ? NotFound() : Ok(entities);
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<ApplicationDto>> GetById(Guid opportunityId, Guid id, CancellationToken cancellationToken)
    {
        var entity = await service.GetByIdAsync(opportunityId, id, cancellationToken);
        return entity is null ? NotFound() : Ok(entity);
    }

    [HttpPost]
    public async Task<ActionResult<ApplicationDto>> Create(Guid opportunityId,
        CreateApplicationRequest request, CancellationToken cancellationToken)
    {
        var (found, entity, error) = await service.CreateAsync(opportunityId, request, cancellationToken);
        if (!found)
            return NotFound();
        if (error is not null)
            return Problem(detail: error, statusCode: StatusCodes.Status400BadRequest);
        return CreatedAtAction(nameof(GetById), new { opportunityId, id = entity!.Id }, entity);
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid opportunityId, Guid id,
        UpdateApplicationRequest request, CancellationToken cancellationToken)
    {
        var (found, error) = await service.UpdateAsync(opportunityId, id, request, cancellationToken);
        if (!found)
            return NotFound();
        return error is null ? NoContent() : Problem(detail: error, statusCode: StatusCodes.Status400BadRequest);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid opportunityId, Guid id, CancellationToken cancellationToken)
        => await service.DeleteAsync(opportunityId, id, cancellationToken) ? NoContent() : NotFound();
}
