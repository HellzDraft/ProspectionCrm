using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using ProspectionCrm.Api.Dtos.Pipelines;
using ProspectionCrm.Api.Services;

namespace ProspectionCrm.Api.Controllers;

[ApiController]
[Route("api/pipelines")]
public class PipelinesController(IPipelineService pipelineService) : ControllerBase
{
    // Local to the portable format: existing API contracts retain their JSON behavior.
    private static readonly JsonSerializerOptions ImportJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    [HttpGet("{pipelineId:guid}/export")]
    [ProducesResponseType<PipelineTransferDocument>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PipelineTransferDocument>> Export(Guid pipelineId, CancellationToken cancellationToken)
    {
        var document = await pipelineService.ExportAsync(pipelineId, cancellationToken);
        return document is null ? NotFound() : Ok(document);
    }

    [HttpPost("import")]
    [ProducesResponseType<PipelineDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<PipelineDto>> Import(JsonElement body, CancellationToken cancellationToken)
    {
        PipelineTransferDocument? document;
        try
        {
            document = body.Deserialize<PipelineTransferDocument>(ImportJsonOptions);
        }
        catch (JsonException exception)
        {
            return Problem(detail: $"Invalid pipeline document: {exception.Message}", statusCode: StatusCodes.Status400BadRequest);
        }

        var (pipeline, error) = await pipelineService.ImportAsync(document, cancellationToken);
        if (error is not null)
            return Problem(detail: error, statusCode: StatusCodes.Status400BadRequest);
        return CreatedAtAction(nameof(GetById), new { id = pipeline!.Id }, pipeline);
    }

    [HttpGet]
    [ProducesResponseType<IReadOnlyList<PipelineDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<PipelineDto>>> GetAll(
        CancellationToken cancellationToken, [FromQuery] bool includeArchived = false)
        => Ok(await pipelineService.GetAllAsync(includeArchived, cancellationToken));

    [HttpGet("{id:guid}")]
    [ProducesResponseType<PipelineDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PipelineDto>> GetById(
        Guid id, CancellationToken cancellationToken, [FromQuery] bool includeArchivedStages = false)
    {
        var pipeline = await pipelineService.GetByIdAsync(id, includeArchivedStages, cancellationToken);
        return pipeline is null ? NotFound() : Ok(pipeline);
    }

    [HttpPost]
    [ProducesResponseType<PipelineDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<PipelineDto>> Create(PipelineWriteRequest request, CancellationToken cancellationToken)
    {
        var (pipeline, error) = await pipelineService.CreateAsync(request, cancellationToken);
        if (error is not null)
            return Problem(detail: error, statusCode: StatusCodes.Status400BadRequest);
        return CreatedAtAction(nameof(GetById), new { id = pipeline!.Id }, pipeline);
    }

    [HttpPost("{pipelineId:guid}/clone")]
    [ProducesResponseType<PipelineDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<PipelineDto>> Clone(
        Guid pipelineId, PipelineCloneRequest request, CancellationToken cancellationToken)
    {
        var result = await pipelineService.CloneAsync(pipelineId, request, cancellationToken);
        return result.Status switch
        {
            PipelineCloneStatus.Succeeded => CreatedAtAction(nameof(GetById), new { id = result.Pipeline!.Id }, result.Pipeline),
            PipelineCloneStatus.NotFound => NotFound(),
            PipelineCloneStatus.InvalidInput => Problem(detail: result.Error, statusCode: StatusCodes.Status400BadRequest),
            PipelineCloneStatus.Conflict => Problem(detail: result.Error, statusCode: StatusCodes.Status409Conflict),
            _ => throw new InvalidOperationException("Unexpected pipeline clone status.")
        };
    }

    [HttpPut("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Update(Guid id, PipelineWriteRequest request, CancellationToken cancellationToken)
    {
        var (found, error) = await pipelineService.UpdateAsync(id, request, cancellationToken);
        if (!found)
            return NotFound();
        return error is not null ? Problem(detail: error, statusCode: StatusCodes.Status400BadRequest) : NoContent();
    }

    [HttpPost("{id:guid}/archive")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Archive(Guid id, CancellationToken cancellationToken)
        => await pipelineService.ArchiveAsync(id, cancellationToken) ? NoContent() : NotFound();

    [HttpPost("{id:guid}/restore")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Restore(Guid id, CancellationToken cancellationToken)
        => await pipelineService.RestoreAsync(id, cancellationToken) ? NoContent() : NotFound();

    [HttpPost("{id:guid}/set-default")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SetDefault(Guid id, CancellationToken cancellationToken)
    {
        var (found, error) = await pipelineService.SetDefaultAsync(id, cancellationToken);
        if (!found)
            return NotFound();
        return error is not null ? Problem(detail: error, statusCode: StatusCodes.Status409Conflict) : NoContent();
    }
}
