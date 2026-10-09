using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Npgsql;
using ProspectionCrm.Api.Dtos.AutomationActionRequests;
using ProspectionCrm.Api.Services.Automation;

namespace ProspectionCrm.Api.Controllers;

[ApiController]
[Route("api/automation-action-requests")]
[AutomationActionRequestErrors]
public sealed class AutomationActionRequestsController(AutomationActionRequestService service) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<AutomationActionRequestsPageDto>> List(CancellationToken token, int offset = 0,
        int limit = 50, string? status = null, string? decisionRequirement = null, Guid? automationRuleId = null,
        Guid? automationJobId = null, string? actionType = null) => Result(await service.ListAsync(offset, limit,
            status, decisionRequirement, automationRuleId, automationJobId, actionType, token));
    [HttpGet("{id:guid}")]
    public async Task<ActionResult<AutomationActionRequestDto>> Get(Guid id, CancellationToken token)
        => Result(await service.GetAsync(id, token));
    [HttpPost("{id:guid}/approve")]
    [RequestSizeLimit(16384)]
    public async Task<ActionResult<AutomationActionRequestDto>> Approve(Guid id, DecideAutomationActionRequest request,
        CancellationToken token) => Result(await service.DecideAsync(id, request, true, token));
    [HttpPost("{id:guid}/reject")]
    [RequestSizeLimit(16384)]
    public async Task<ActionResult<AutomationActionRequestDto>> Reject(Guid id, DecideAutomationActionRequest request,
        CancellationToken token) => Result(await service.DecideAsync(id, request, false, token));
    private ActionResult<T> Result<T>(AutomationJobResult<T> result) => result.Value is not null ? Ok(result.Value)
        : Problem(statusCode: result.Status, title: "Automation action request failed",
            extensions: new Dictionary<string, object?> { ["code"] = result.Code });
}

public sealed class AutomationActionRequestErrorsAttribute : ActionFilterAttribute, IExceptionFilter
{
    public AutomationActionRequestErrorsAttribute() => Order = -3000;
    public override void OnActionExecuting(ActionExecutingContext context)
    {
        if (!context.ModelState.IsValid) context.Result = Failure(400, "InvalidRequest");
    }
    public void OnException(ExceptionContext context)
    {
        if (context.Exception is OperationCanceledException && context.HttpContext.RequestAborted.IsCancellationRequested) return;
        var error = context.Exception.GetBaseException();
        var conflict = error is PostgresException { SqlState: "40001" or "40P01" or "23505" or "23503" };
        context.Result = Failure(conflict ? 409 : 500, conflict ? "ConcurrentActionRequestChange" : "AutomationActionRequestInternalError");
        context.ExceptionHandled = true;
        context.HttpContext.RequestServices.GetRequiredService<ILogger<AutomationActionRequestErrorsAttribute>>()
            .LogError("Automation action request failed: {ExceptionType}", error.GetType().Name);
    }
    private static ObjectResult Failure(int status, string code)
    {
        var problem = new ProblemDetails { Status = status, Title = "Automation action request failed" };
        problem.Extensions["code"] = code;
        return new(problem) { StatusCode = status, ContentTypes = { "application/problem+json" } };
    }
}
