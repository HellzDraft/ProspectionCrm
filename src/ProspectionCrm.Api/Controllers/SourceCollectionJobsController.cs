using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using ProspectionCrm.Api.Dtos.CollectionJobs;
using ProspectionCrm.Api.Services.Collection;

namespace ProspectionCrm.Api.Controllers;

[ApiController]
[Route("api/source-collection-jobs")]
[CollectionJobValidation]
[CollectionApiException("CollectionJobInternalError")]
[ProducesResponseType<ProblemDetails>(400)]
[ProducesResponseType<ProblemDetails>(404)]
[ProducesResponseType<ProblemDetails>(409)]
[ProducesResponseType<ProblemDetails>(500)]
public sealed class SourceCollectionJobsController(ISourceCollectionJobService service) : ControllerBase
{
    [HttpPost("/api/saved-searches/{savedSearchId:guid}/collection-jobs")]
    [RequestSizeLimit(4096)]
    [ProducesResponseType<SourceCollectionJobDto>(202)]
    public async Task<ActionResult<SourceCollectionJobDto>> Enqueue(Guid savedSearchId,
        EnqueueSourceCollectionJobRequest request, CancellationToken token)
    {
        var result = await service.EnqueueAsync(savedSearchId, request, token);
        return result.Status == SourceCollectionJobStatus.Succeeded
            ? AcceptedAtAction(nameof(GetById), new { id = result.Value!.Id }, result.Value) : Failure(result);
    }

    [HttpGet("{id:guid}")]
    [ProducesResponseType<SourceCollectionJobDto>(200)]
    public async Task<ActionResult<SourceCollectionJobDto>> GetById(Guid id, CancellationToken token)
    {
        var result = await service.GetAsync(id, token);
        return result.Status == SourceCollectionJobStatus.Succeeded ? Ok(result.Value) : Failure(result);
    }

    [HttpGet]
    [ProducesResponseType<SourceCollectionJobsPageDto>(200)]
    public async Task<ActionResult<SourceCollectionJobsPageDto>> List(CancellationToken token, int offset = 0,
        int limit = 50, string? statusCode = null, Guid? savedSearchId = null, string? triggerTypeCode = null)
    {
        var result = await service.ListAsync(offset, limit, statusCode, savedSearchId, triggerTypeCode, token);
        return result.Status == SourceCollectionJobStatus.Succeeded ? Ok(result.Value) : Failure(result);
    }

    [HttpPost("{id:guid}/cancel")]
    [ProducesResponseType(204)]
    public async Task<ActionResult> Cancel(Guid id, CancellationToken token)
    {
        var result = await service.CancelAsync(id, token);
        return result.Status == SourceCollectionJobStatus.Succeeded ? NoContent() : Failure(result);
    }

    private ObjectResult Failure<T>(SourceCollectionJobResult<T> result)
    {
        var extensions = new Dictionary<string, object?> { ["code"] = result.Code };
        if (result.ExistingJobId is { } id) extensions["existingJobId"] = id;
        return Problem(statusCode: result.Status switch
        {
            SourceCollectionJobStatus.NotFound => 404,
            SourceCollectionJobStatus.InvalidRequest => 400,
            _ => 409
        }, title: "Source collection job request failed", detail: $"Source collection job request could not complete ({result.Code}).",
            extensions: extensions);
    }
}

public sealed class CollectionJobValidationAttribute : ActionFilterAttribute
{
    public CollectionJobValidationAttribute() => Order = -3000;
    public override void OnActionExecuting(ActionExecutingContext context)
    {
        if (context.ModelState.IsValid) return;
        var problem = new ProblemDetails { Status = 400, Title = "Source collection job request failed", Detail = "The request is invalid." };
        problem.Extensions["code"] = "InvalidRequest";
        context.Result = new BadRequestObjectResult(problem);
    }
}
