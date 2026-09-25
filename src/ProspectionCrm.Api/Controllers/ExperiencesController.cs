using Microsoft.AspNetCore.Mvc;
using ProspectionCrm.Api.Dtos.Experiences;
using ProspectionCrm.Api.Services;

namespace ProspectionCrm.Api.Controllers;

[ApiController]
[Route("api/experiences")]
public class ExperiencesController(IExperienceService service) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<ExperienceDto>>> GetAll(CancellationToken cancellationToken)
        => Ok(await service.GetAllAsync(cancellationToken));

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<ExperienceDto>> GetById(Guid id, CancellationToken cancellationToken)
    {
        var entity = await service.GetByIdAsync(id, cancellationToken);
        return entity is null ? NotFound() : Ok(entity);
    }

    [HttpPost]
    public async Task<ActionResult<ExperienceDto>> Create(CreateExperienceRequest request, CancellationToken cancellationToken)
    {
        var (entity, error) = await service.CreateAsync(request, cancellationToken);
        if (error is not null)
            return Problem(detail: error, statusCode: StatusCodes.Status400BadRequest);
        return CreatedAtAction(nameof(GetById), new { id = entity!.Id }, entity);
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, UpdateExperienceRequest request, CancellationToken cancellationToken)
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
