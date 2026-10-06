using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using ProspectionCrm.Api.Dtos.Collection;
using ProspectionCrm.Api.Services.Collection;

namespace ProspectionCrm.Api.Controllers;

[ApiController]
[Route("api/saved-searches/{savedSearchId:guid}/collect")]
[CollectionBodyValidation]
public sealed class SourceCollectionController(ISourceCollectionService service) : ControllerBase
{
    [HttpPost]
    [RequestSizeLimit(4096)]
    [ProducesResponseType<SourceCollectionDto>(201)]
    [ProducesResponseType<ProblemDetails>(400)]
    [ProducesResponseType<ProblemDetails>(404)]
    [ProducesResponseType<ProblemDetails>(409)]
    [ProducesResponseType<ProblemDetails>(422)]
    [ProducesResponseType<ProblemDetails>(502)]
    [ProducesResponseType<ProblemDetails>(504)]
    [ProducesResponseType<ProblemDetails>(500)]
    public async Task<ActionResult<SourceCollectionDto>> Collect(Guid savedSearchId, CollectSavedSearchRequest request, CancellationToken token)
    {
        var result = await service.CollectAsync(savedSearchId, request, token);
        if (result.StatusCode == 201)
            return CreatedAtAction(nameof(SourceExecutionsController.GetById), "SourceExecutions",
                new { id = result.Value!.Ingestion.Execution.Id }, result.Value);
        return Problem(statusCode: result.StatusCode, title: "Source collection failed", detail: result.Error!.Detail,
            extensions: new Dictionary<string, object?>
            {
                ["code"] = result.Error.Code, ["executionId"] = result.ExecutionId,
                ["itemIndex"] = result.ItemIndex, ["upstreamStatusCode"] = result.Error.UpstreamStatusCode
            });
    }
}

// Run before ApiController's automatic validation; no rejected raw values in this endpoint's errors.
public sealed class CollectionBodyValidationAttribute : ActionFilterAttribute
{
    public CollectionBodyValidationAttribute() => Order = -3000;
    public override void OnActionExecuting(ActionExecutingContext context)
    {
        if (context.ModelState.IsValid) return;
        var problem = new ProblemDetails { Status = 400, Title = "Source collection failed", Detail = "A valid pipelineStageId is required." };
        problem.Extensions["code"] = "InvalidRequest";
        problem.Extensions["executionId"] = null;
        problem.Extensions["itemIndex"] = null;
        problem.Extensions["upstreamStatusCode"] = null;
        context.Result = new BadRequestObjectResult(problem);
    }
}
