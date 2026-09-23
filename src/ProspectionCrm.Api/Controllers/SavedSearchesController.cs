using Microsoft.AspNetCore.Mvc;
using ProspectionCrm.Api.Dtos.SavedSearches;
using ProspectionCrm.Api.Services;

namespace ProspectionCrm.Api.Controllers;

[ApiController]
[Route("api/saved-searches")]
public class SavedSearchesController(ISavedSearchService service) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<SavedSearchDto>>> GetAll(CancellationToken cancellationToken,
        [FromQuery] bool includeArchived = false, [FromQuery] Guid? pipelineId = null, [FromQuery] Guid? sourceConfigurationId = null, [FromQuery] bool? enabled = null)
        => Ok(await service.GetAllAsync(includeArchived, pipelineId, sourceConfigurationId, enabled, cancellationToken));

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<SavedSearchDto>> GetById(Guid id, CancellationToken cancellationToken)
    {
        var entity = await service.GetByIdAsync(id, cancellationToken);
        return entity is null ? NotFound() : Ok(entity);
    }

    [HttpPost]
    public async Task<ActionResult<SavedSearchDto>> Create(CreateSavedSearchRequest request, CancellationToken cancellationToken)
    {
        var (entity, error) = await service.CreateAsync(request, cancellationToken);
        if (error is not null)
            return Problem(detail: error, statusCode: StatusCodes.Status400BadRequest);
        return CreatedAtAction(nameof(GetById), new { id = entity!.Id }, entity);
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, UpdateSavedSearchRequest request, CancellationToken cancellationToken)
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
