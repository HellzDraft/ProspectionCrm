using Microsoft.AspNetCore.Mvc;
using ProspectionCrm.Api.Dtos.Documents;
using ProspectionCrm.Api.Services;

namespace ProspectionCrm.Api.Controllers;

[ApiController]
[Route("api/documents")]
public class DocumentsController(IDocumentService service) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<DocumentDto>>> GetAll(CancellationToken cancellationToken,
        [FromQuery] bool includeArchived = false, [FromQuery] string? kindCode = null)
        => Ok(await service.GetAllAsync(includeArchived, kindCode, cancellationToken));

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<DocumentDto>> GetById(Guid id, CancellationToken cancellationToken)
    {
        var document = await service.GetByIdAsync(id, cancellationToken);
        return document is null ? NotFound() : Ok(document);
    }

    [HttpPost]
    public async Task<ActionResult<DocumentDto>> Create(CreateDocumentRequest request, CancellationToken cancellationToken)
    {
        var (document, error) = await service.CreateAsync(request, cancellationToken);
        if (error is not null)
            return Problem(detail: error, statusCode: StatusCodes.Status400BadRequest);
        return CreatedAtAction(nameof(GetById), new { id = document!.Id }, document);
    }

    [HttpPost("{id:guid}/archive")]
    public async Task<IActionResult> Archive(Guid id, CancellationToken cancellationToken)
    {
        var (found, error) = await service.ArchiveAsync(id, cancellationToken);
        if (!found)
            return NotFound();
        return error is null ? NoContent() : Problem(detail: error, statusCode: StatusCodes.Status400BadRequest);
    }

    [HttpPost("{id:guid}/restore")]
    public async Task<IActionResult> Restore(Guid id, CancellationToken cancellationToken)
        => await service.RestoreAsync(id, cancellationToken) ? NoContent() : NotFound();
}
