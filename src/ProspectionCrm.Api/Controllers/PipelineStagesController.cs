using Microsoft.AspNetCore.Mvc;
using ProspectionCrm.Api.Dtos.Pipelines;
using ProspectionCrm.Api.Services;

namespace ProspectionCrm.Api.Controllers;

[ApiController]
[Route("api/pipelines/{pipelineId:guid}/stages")]
public class PipelineStagesController(IPipelineStageService stageService) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<PipelineStageDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IReadOnlyList<PipelineStageDto>>> GetAll(
        Guid pipelineId, CancellationToken cancellationToken, [FromQuery] bool includeArchived = false)
    {
        var stages = await stageService.GetAllAsync(pipelineId, includeArchived, cancellationToken);
        return stages is null ? NotFound() : Ok(stages);
    }

    [HttpGet("{stageId:guid}")]
    [ProducesResponseType<PipelineStageDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PipelineStageDto>> GetById(Guid pipelineId, Guid stageId, CancellationToken cancellationToken)
    {
        var stage = await stageService.GetByIdAsync(pipelineId, stageId, cancellationToken);
        return stage is null ? NotFound() : Ok(stage);
    }

    [HttpPost]
    [ProducesResponseType<PipelineStageDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Create(Guid pipelineId, PipelineStageWriteRequest request, CancellationToken cancellationToken)
    {
        var result = await stageService.CreateAsync(pipelineId, request, cancellationToken);
        return result.Status == PipelineStageWriteStatus.Succeeded
            ? CreatedAtAction(nameof(GetById), new { pipelineId, stageId = result.Stage!.Id }, result.Stage)
            : WriteResult(result);
    }

    [HttpPut("{stageId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Update(
        Guid pipelineId, Guid stageId, PipelineStageWriteRequest request, CancellationToken cancellationToken)
        => WriteResult(await stageService.UpdateAsync(pipelineId, stageId, request, cancellationToken));

    [HttpPost("{stageId:guid}/archive")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Archive(Guid pipelineId, Guid stageId, CancellationToken cancellationToken)
        => WriteResult(await stageService.ArchiveAsync(pipelineId, stageId, cancellationToken));

    [HttpPost("{stageId:guid}/restore")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Restore(Guid pipelineId, Guid stageId, CancellationToken cancellationToken)
        => WriteResult(await stageService.RestoreAsync(pipelineId, stageId, cancellationToken));

    [HttpPut("order")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Reorder(
        Guid pipelineId, PipelineStageOrderRequest request, CancellationToken cancellationToken)
        => WriteResult(await stageService.ReorderAsync(pipelineId, request, cancellationToken));

    private IActionResult WriteResult(PipelineStageWriteResult result) => result.Status switch
    {
        PipelineStageWriteStatus.Succeeded => NoContent(),
        PipelineStageWriteStatus.NotFound => NotFound(),
        PipelineStageWriteStatus.InvalidInput => Problem(detail: result.Error, statusCode: StatusCodes.Status400BadRequest),
        PipelineStageWriteStatus.Conflict => Problem(detail: result.Error, statusCode: StatusCodes.Status409Conflict),
        _ => throw new InvalidOperationException("Unexpected stage write status.")
    };
}
