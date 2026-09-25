using Microsoft.AspNetCore.Mvc;
using ProspectionCrm.Api.Dtos.AiPromptTemplates;
using ProspectionCrm.Api.Dtos.AiPromptVersions;
using ProspectionCrm.Api.Services;

namespace ProspectionCrm.Api.Controllers;

[ApiController]
[Route("api/ai-prompt-templates")]
public class AiPromptTemplatesController(IAiPromptTemplateService service) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<AiPromptTemplateDto>>> GetAll(CancellationToken cancellationToken,
        [FromQuery] bool includeArchived = false, [FromQuery] string? purposeCode = null)
        => Ok(await service.GetAllAsync(includeArchived, purposeCode, cancellationToken));

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<AiPromptTemplateDto>> GetById(Guid id, CancellationToken cancellationToken)
    {
        var item = await service.GetByIdAsync(id, cancellationToken);
        return item is null ? NotFound() : Ok(item);
    }

    [HttpPost]
    public async Task<ActionResult<AiPromptTemplateDto>> Create(CreateAiPromptTemplateRequest request, CancellationToken cancellationToken)
    {
        var (item, error) = await service.CreateAsync(request, cancellationToken);
        if (error is not null)
            return Problem(detail: error, statusCode: StatusCodes.Status400BadRequest);
        return CreatedAtAction(nameof(GetById), new { id = item!.Id }, item);
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, UpdateAiPromptTemplateRequest request, CancellationToken cancellationToken)
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

    [HttpGet("{templateId:guid}/versions")]
    public async Task<ActionResult<IReadOnlyList<AiPromptVersionDto>>> GetVersions(Guid templateId, CancellationToken cancellationToken)
    {
        var versions = await service.GetVersionsAsync(templateId, cancellationToken);
        return versions is null ? NotFound() : Ok(versions);
    }

    [HttpGet("{templateId:guid}/versions/{versionNumber:int}")]
    public async Task<ActionResult<AiPromptVersionDto>> GetVersion(Guid templateId, int versionNumber, CancellationToken cancellationToken)
    {
        var version = await service.GetVersionAsync(templateId, versionNumber, cancellationToken);
        return version is null ? NotFound() : Ok(version);
    }

    [HttpPost("{templateId:guid}/versions")]
    public async Task<ActionResult<AiPromptVersionDto>> CreateVersion(
        Guid templateId, CreateAiPromptVersionRequest request, CancellationToken cancellationToken)
    {
        var (found, version, error) = await service.CreateVersionAsync(templateId, request, cancellationToken);
        if (!found)
            return NotFound();
        if (error is not null)
            return Problem(detail: error, statusCode: StatusCodes.Status400BadRequest);
        return CreatedAtAction(nameof(GetVersion), new { templateId, versionNumber = version!.VersionNumber }, version);
    }
}
