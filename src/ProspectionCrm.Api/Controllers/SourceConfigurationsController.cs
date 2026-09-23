using Microsoft.AspNetCore.Mvc;
using ProspectionCrm.Api.Dtos.SourceConfigurations;
using ProspectionCrm.Api.Services;

namespace ProspectionCrm.Api.Controllers;

[ApiController]
[Route("api/source-configurations")]
public class SourceConfigurationsController(ISourceConfigurationService service) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<SourceConfigurationDto>>> GetAll(CancellationToken cancellationToken,
        [FromQuery] bool includeArchived = false)
        => Ok(await service.GetAllAsync(includeArchived, cancellationToken));

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<SourceConfigurationDto>> GetById(Guid id, CancellationToken cancellationToken)
    {
        var entity = await service.GetByIdAsync(id, cancellationToken);
        return entity is null ? NotFound() : Ok(entity);
    }

    [HttpPost]
    public async Task<ActionResult<SourceConfigurationDto>> Create(CreateSourceConfigurationRequest request, CancellationToken cancellationToken)
    {
        var (entity, error) = await service.CreateAsync(request, cancellationToken);
        if (error is not null)
            return Problem(detail: error, statusCode: StatusCodes.Status400BadRequest);
        return CreatedAtAction(nameof(GetById), new { id = entity!.Id }, entity);
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, UpdateSourceConfigurationRequest request, CancellationToken cancellationToken)
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
