using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using ProspectionCrm.Api.Dtos.IngestionHistory;
using ProspectionCrm.Api.Services;

namespace ProspectionCrm.Api.Controllers;

[ApiController]
[HistoryQueryBindingErrors]
[ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
[ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
public sealed class IngestionHistoryController(IIngestionHistoryReadService service) : ControllerBase
{
    [HttpGet("api/source-executions/{id:guid}/history")]
    [ProducesResponseType(typeof(SourceExecutionHistoryDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<SourceExecutionHistoryDto>> History(Guid id, CancellationToken cancellationToken) =>
        Respond(await service.GetExecutionHistoryAsync(id, cancellationToken));

    [HttpGet("api/source-executions/{id:guid}/items")]
    [ProducesResponseType(typeof(SourceExecutionItemsPageDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<SourceExecutionItemsPageDto>> Items(Guid id,
        [FromQuery] ExecutionItemsQuery query, CancellationToken cancellationToken) =>
        Respond(await service.GetExecutionItemsAsync(id, query, cancellationToken));

    [HttpGet("api/opportunities/{opportunityId:guid}/observations")]
    [ProducesResponseType(typeof(OpportunityObservationsPageDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<OpportunityObservationsPageDto>> Observations(Guid opportunityId,
        [FromQuery] OpportunityObservationsQuery query, CancellationToken cancellationToken) =>
        Respond(await service.GetOpportunityObservationsAsync(opportunityId, query, cancellationToken));

    [HttpGet("api/opportunities/{opportunityId:guid}/sources/{sourceId:guid}/observations")]
    [ProducesResponseType(typeof(OpportunitySourceObservationsPageDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<OpportunitySourceObservationsPageDto>> SourceObservations(Guid opportunityId, Guid sourceId,
        [FromQuery] OpportunitySourceObservationsQuery query, CancellationToken cancellationToken) =>
        Respond(await service.GetSourceObservationsAsync(opportunityId, sourceId, query, cancellationToken));

    private ActionResult<T> Respond<T>(IngestionHistoryReadResult<T> result) => result.Status == IngestionHistoryReadStatus.Succeeded
        ? Ok(result.Value)
        : Problem(statusCode: result.Status == IngestionHistoryReadStatus.NotFound ? 404 : 400,
            detail: result.Detail, extensions: new Dictionary<string, object?> { ["code"] = result.Code?.ToString() });
}

// Apply only to these new read routes, before ApiController's automatic model-state filter.
// Malformed typed query values must also have a controlled code, without echoing input.
[AttributeUsage(AttributeTargets.Class)]
internal sealed class HistoryQueryBindingErrorsAttribute : ActionFilterAttribute
{
    public HistoryQueryBindingErrorsAttribute() => Order = -3000;
    public override void OnActionExecuting(ActionExecutingContext context)
    {
        if (context.ModelState.IsValid) return;
        var fields = context.ModelState.Where(x => x.Value?.Errors.Count > 0)
            .Select(x => x.Key.Split('.').Last()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var code = fields.Contains("offset") || fields.Contains("limit") ? IngestionHistoryReadErrorCode.InvalidPagination
            : fields.Contains("from") || fields.Contains("to") ? IngestionHistoryReadErrorCode.InvalidDateRange
            : IngestionHistoryReadErrorCode.InvalidReference;
        var problem = new ProblemDetails { Status = 400, Title = "Invalid history query", Detail = "A query parameter has an invalid format." };
        problem.Extensions["code"] = code.ToString();
        context.Result = new ObjectResult(problem) { StatusCode = 400 };
    }
}
