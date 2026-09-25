using Microsoft.AspNetCore.Mvc;
using ProspectionCrm.Api.Dtos.Projects;
using ProspectionCrm.Api.Services;

namespace ProspectionCrm.Api.Controllers;

[ApiController]
[Route("api/projects")]
public class ProjectsController(IProjectService service) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<ProjectDto>>> GetAll(CancellationToken cancellationToken, [FromQuery] bool includeArchived = false)
        => Ok(await service.GetAllAsync(includeArchived, cancellationToken));

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<ProjectDto>> GetById(Guid id, CancellationToken cancellationToken)
    {
        var entity = await service.GetByIdAsync(id, cancellationToken);
        return entity is null ? NotFound() : Ok(entity);
    }

    [HttpPost]
    public async Task<ActionResult<ProjectDto>> Create(CreateProjectRequest request, CancellationToken cancellationToken)
    {
        var (entity, error) = await service.CreateAsync(request, cancellationToken);
        if (error is not null)
            return Problem(detail: error, statusCode: StatusCodes.Status400BadRequest);
        return CreatedAtAction(nameof(GetById), new { id = entity!.Id }, entity);
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, UpdateProjectRequest request, CancellationToken cancellationToken)
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
        => await service.RestoreAsync(id, cancellationToken) ? NoContent() : NotFound();

    [HttpPut("{projectId:guid}/skills/{skillId:guid}")]
    public async Task<IActionResult> PutSkill(Guid projectId, Guid skillId, CancellationToken cancellationToken)
    {
        var (found, error) = await service.PutSkillAsync(projectId, skillId, cancellationToken);
        if (!found)
            return NotFound();
        return error is null ? NoContent() : Problem(detail: error, statusCode: StatusCodes.Status400BadRequest);
    }

    [HttpDelete("{projectId:guid}/skills/{skillId:guid}")]
    public async Task<IActionResult> RemoveSkill(Guid projectId, Guid skillId, CancellationToken cancellationToken)
        => await service.RemoveSkillAsync(projectId, skillId, cancellationToken) ? NoContent() : NotFound();
}
