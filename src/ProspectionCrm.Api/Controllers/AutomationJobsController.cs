using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using ProspectionCrm.Api.Dtos.AutomationJobs;
using ProspectionCrm.Api.Services.Automation;

namespace ProspectionCrm.Api.Controllers;

[ApiController]
[Route("api/automation-jobs")]
[AutomationJobValidation]
[AutomationJobException]
[ProducesResponseType<ProblemDetails>(400)]
[ProducesResponseType<ProblemDetails>(404)]
[ProducesResponseType<ProblemDetails>(409)]
[ProducesResponseType<ProblemDetails>(500)]
public sealed class AutomationJobsController(AutomationJobService service) : ControllerBase
{
    [HttpPost]
    [RequestSizeLimit(131072)]
    [ProducesResponseType<AutomationJobDto>(201)]
    [ProducesResponseType<AutomationJobDto>(200)]
    public async Task<ActionResult<AutomationJobDto>> Enqueue(EnqueueAutomationJobRequest request, CancellationToken token)
    {
        var result = await service.EnqueueAsync(request, token);
        if (result.Value is null) return Failure(result);
        return result.Status == 201 ? CreatedAtAction(nameof(GetById), new { id = result.Value.Id }, result.Value) : Ok(result.Value);
    }

    [HttpGet("{id:guid}")]
    [ProducesResponseType<AutomationJobDto>(200)]
    public async Task<ActionResult<AutomationJobDto>> GetById(Guid id, CancellationToken token)
    {
        var result = await service.GetAsync(id, token);
        return result.Value is null ? Failure(result) : Ok(result.Value);
    }

    [HttpGet]
    [ProducesResponseType<AutomationJobsPageDto>(200)]
    public async Task<ActionResult<AutomationJobsPageDto>> List(CancellationToken token, int offset = 0, int limit = 50,
        string? status = null, Guid? automationRuleId = null, string? triggerType = null, string? actionCategory = null)
    {
        var result = await service.ListAsync(offset, limit, status, automationRuleId, triggerType, actionCategory, token);
        return result.Value is null ? Failure(result) : Ok(result.Value);
    }

    private ObjectResult Failure<T>(AutomationJobResult<T> result) => Problem(statusCode: result.Status,
        title: "Automation job request failed", detail: "The automation job request could not be completed.",
        extensions: new Dictionary<string, object?> { ["code"] = result.Code });
}

public sealed class AutomationJobValidationAttribute : ActionFilterAttribute
{
    public AutomationJobValidationAttribute() => Order = -3000;
    public override void OnActionExecuting(ActionExecutingContext context)
    {
        if (context.ModelState.IsValid) return;
        var problem = new ProblemDetails { Status = 400, Title = "Automation job request failed", Detail = "The request is invalid. Unknown fields are not accepted." };
        problem.Extensions["code"] = "InvalidRequest";
        context.Result = new ObjectResult(problem) { StatusCode = 400, ContentTypes = { "application/problem+json" } };
    }
}

public sealed class AutomationJobExceptionAttribute : ExceptionFilterAttribute
{
    public override void OnException(ExceptionContext context)
    {
        if (context.Exception is OperationCanceledException && context.HttpContext.RequestAborted.IsCancellationRequested) return;
        // Do not attach the request or ContextJson to logs, even for malformed input.
        context.HttpContext.RequestServices.GetRequiredService<ILogger<AutomationJobExceptionAttribute>>()
            .LogError("Automation job request failed with {ExceptionType}", context.Exception.GetType().Name);
        var problem = new ProblemDetails { Status = 500, Title = "Automation job request failed", Detail = "The request could not be completed." };
        problem.Extensions["code"] = "AutomationJobInternalError";
        context.Result = new ObjectResult(problem) { StatusCode = 500, ContentTypes = { "application/problem+json" } };
        context.ExceptionHandled = true;
    }
}
