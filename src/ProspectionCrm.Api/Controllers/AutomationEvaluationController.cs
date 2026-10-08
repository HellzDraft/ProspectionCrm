using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using ProspectionCrm.Api.Dtos.AutomationEvents;
using ProspectionCrm.Api.Services.Automation;

namespace ProspectionCrm.Api.Controllers;

[ApiController]
[AutomationEvaluationValidation]
[AutomationEvaluationException]
[ProducesResponseType<ProblemDetails>(400)]
[ProducesResponseType<ProblemDetails>(404)]
[ProducesResponseType<ProblemDetails>(409)]
public sealed class AutomationEvaluationController(AutomationEvaluationService service) : ControllerBase
{
    [HttpPost("api/automation-events")]
    [RequestSizeLimit(131072)]
    [ProducesResponseType<AutomationDispatchSummary>(200)]
    public async Task<ActionResult<AutomationDispatchSummary>> Dispatch(DispatchAutomationEventRequest request, CancellationToken token)
    {
        var result = await service.DispatchAsync(request, token);
        return result.Value is null ? Failure(result) : Ok(result.Value);
    }

    [HttpGet("api/automation-jobs/{id:guid}/evaluation-preview")]
    [ProducesResponseType<AutomationEvaluationResult>(200)]
    public async Task<ActionResult<AutomationEvaluationResult>> Preview(Guid id, CancellationToken token)
    {
        var result = await service.PreviewAsync(id, token);
        return result.Value is null ? Failure(result) : Ok(result.Value);
    }

    private ObjectResult Failure<T>(AutomationJobResult<T> result) => Problem(statusCode: result.Status,
        title: "Automation evaluation request failed", detail: "The request could not be completed.",
        extensions: new Dictionary<string, object?> { ["code"] = result.Code });
}

public sealed class AutomationEvaluationValidationAttribute : ActionFilterAttribute
{
    public AutomationEvaluationValidationAttribute() => Order = -3000;
    public override void OnActionExecuting(ActionExecutingContext context)
    {
        if (context.ModelState.IsValid) return;
        var problem = new ProblemDetails { Status = 400, Title = "Invalid automation request", Detail = "The request is invalid. Unknown fields are not accepted." };
        problem.Extensions["code"] = "InvalidRequest";
        context.Result = new ObjectResult(problem) { StatusCode = 400, ContentTypes = { "application/problem+json" } };
    }
}

public sealed class AutomationEvaluationExceptionAttribute : ExceptionFilterAttribute
{
    public override void OnException(ExceptionContext context)
    {
        if (context.Exception is OperationCanceledException && context.HttpContext.RequestAborted.IsCancellationRequested) return;
        // Do not log payloads, configuration, exception messages or client values.
        context.HttpContext.RequestServices.GetRequiredService<ILogger<AutomationEvaluationExceptionAttribute>>()
            .LogError("Automation evaluation request failed with {ExceptionType}", context.Exception.GetType().Name);
        var problem = new ProblemDetails { Status = 500, Title = "Automation request failed", Detail = "The request could not be completed." };
        problem.Extensions["code"] = "AutomationEvaluationInternalError";
        context.Result = new ObjectResult(problem) { StatusCode = 500, ContentTypes = { "application/problem+json" } };
        context.ExceptionHandled = true;
    }
}
