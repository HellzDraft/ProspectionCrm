using Microsoft.AspNetCore.Mvc;
using ProspectionCrm.Api.Dtos.Ingestions;
using ProspectionCrm.Api.Services;

namespace ProspectionCrm.Api.Controllers;

[ApiController]
[Route("api/saved-searches/{savedSearchId:guid}/ingestions")]
public sealed class IngestionsController(IIngestionService service) : ControllerBase
{
    [HttpPost]
    [RequestSizeLimit(2_000_000)]
    [ProducesResponseType<IngestionDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<IngestionDto>> Ingest(Guid savedSearchId, IngestionRequest request, CancellationToken cancellationToken)
    {
        var result = await service.IngestAsync(savedSearchId, request, cancellationToken);
        if (result.Status == IngestionStatus.Succeeded)
            return CreatedAtAction(nameof(SourceExecutionsController.GetById), "SourceExecutions",
                new { id = result.Value!.Execution.Id }, result.Value);
        var status = result.Status switch
        {
            IngestionStatus.InvalidRequest => StatusCodes.Status400BadRequest,
            IngestionStatus.NotFound => StatusCodes.Status404NotFound,
            IngestionStatus.Conflict => StatusCodes.Status409Conflict,
            _ => StatusCodes.Status500InternalServerError
        };
        return Problem(statusCode: status, title: "Manual ingestion failed", detail: result.Error!.Detail,
            extensions: new Dictionary<string, object?>
            {
                ["code"] = result.Error.Code.ToString(), ["itemIndex"] = result.Error.ItemIndex,
                ["executionId"] = result.Error.ExecutionId
            });
    }
}
