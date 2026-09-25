using Microsoft.AspNetCore.Mvc;
using ProspectionCrm.Api.Dtos.Skills;
using ProspectionCrm.Api.Services;

namespace ProspectionCrm.Api.Controllers;

[ApiController]
[Route("api/skills")]
public class SkillsController(ISkillService service) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<SkillDto>>> GetAll(CancellationToken cancellationToken, [FromQuery] string? categoryCode = null)
        => Ok(await service.GetAllAsync(categoryCode, cancellationToken));

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<SkillDto>> GetById(Guid id, CancellationToken cancellationToken)
    {
        var entity = await service.GetByIdAsync(id, cancellationToken);
        return entity is null ? NotFound() : Ok(entity);
    }

    [HttpPost]
    public async Task<ActionResult<SkillDto>> Create(CreateSkillRequest request, CancellationToken cancellationToken)
    {
        var (entity, error) = await service.CreateAsync(request, cancellationToken);
        if (error is not null)
            return Problem(detail: error, statusCode: StatusCodes.Status400BadRequest);
        return CreatedAtAction(nameof(GetById), new { id = entity!.Id }, entity);
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, UpdateSkillRequest request, CancellationToken cancellationToken)
    {
        var (found, error) = await service.UpdateAsync(id, request, cancellationToken);
        if (!found)
            return NotFound();
        return error is null ? NoContent() : Problem(detail: error, statusCode: StatusCodes.Status400BadRequest);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        var (found, error) = await service.DeleteAsync(id, cancellationToken);
        if (!found)
            return NotFound();
        return error is null ? NoContent() : Problem(detail: error, statusCode: StatusCodes.Status400BadRequest);
    }
}
