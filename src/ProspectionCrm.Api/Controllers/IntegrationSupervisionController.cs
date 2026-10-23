using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using ProspectionCrm.Api.Services.Integrations;

namespace ProspectionCrm.Api.Controllers;

[ApiController, Authorize, Route("api/integration-supervision")]
[IntegrationSupervisionErrors]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class IntegrationSupervisionController(IntegrationSupervisionService service) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken token) => Ok(await service.SnapshotAsync(token));

    [HttpGet("operations")]
    public async Task<IActionResult> Operations(CancellationToken token, int offset = 0, int limit = 50,
        string? type = null, string? status = null, string? state = null)
    {
        if (offset is < 0 or > 1000000 || limit is < 1 or > 100
            || type is not null && !IntegrationSupervisionService.Types.Contains(type)
            || status is not null && !IntegrationSupervisionService.Statuses.Contains(status)
            || state is not null && !IntegrationSupervisionService.States.Contains(state))
            return IntegrationSupervisionErrorsAttribute.Failure(400, "InvalidRequest");
        return Ok(await service.ListAsync(offset, limit, type, status, state, token));
    }
}

// Do not echo model-binding values, database errors or exception messages into DTOs or logs.
public sealed class IntegrationSupervisionErrorsAttribute : ActionFilterAttribute, IExceptionFilter
{
    public IntegrationSupervisionErrorsAttribute() => Order = -3000;
    public override void OnActionExecuting(ActionExecutingContext context)
    { if (!context.ModelState.IsValid) context.Result = Failure(400, "InvalidRequest"); }
    public void OnException(ExceptionContext context)
    {
        if (context.Exception is OperationCanceledException && context.HttpContext.RequestAborted.IsCancellationRequested) return;
        context.Result = context.Exception is UnauthorizedAccessException ? Failure(403, "WorkspaceUnavailable")
            : Failure(500, "IntegrationSupervisionUnavailable");
        context.ExceptionHandled = true;
    }
    internal static ObjectResult Failure(int status, string code) => new(new ProblemDetails
        { Status = status, Title = "Integration supervision unavailable", Extensions = { ["code"] = code } })
        { StatusCode = status };
}
