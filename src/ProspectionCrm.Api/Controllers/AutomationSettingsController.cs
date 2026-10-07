using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using ProspectionCrm.Api.Dtos.AutomationSettings;
using ProspectionCrm.Api.Services.Automation;

namespace ProspectionCrm.Api.Controllers;

[ApiController]
[Route("api/automation-settings")]
[AutomationSettingsValidation]
[AutomationSettingsException]
[ProducesResponseType<ProblemDetails>(400)]
[ProducesResponseType<ProblemDetails>(409)]
[ProducesResponseType<ProblemDetails>(500)]
public sealed class AutomationSettingsController(AutomationSettingsService service) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<AutomationSettingsDto>> Get(CancellationToken token) => ToResponse(await service.GetAsync(token));
    [HttpPut]
    [RequestSizeLimit(4096)]
    public async Task<ActionResult<AutomationSettingsDto>> Put(UpdateAutomationSettingsRequest request, CancellationToken token)
        => ToResponse(await service.UpdateAsync(request, token));
    private ActionResult<AutomationSettingsDto> ToResponse(AutomationSettingsResult result) => result.Value is not null
        ? Ok(result.Value) : Problem(statusCode: result.Status, title: "Automation settings request failed", detail: result.Detail,
            extensions: new Dictionary<string, object?> { ["code"] = result.Code });
}

public sealed class AutomationSettingsValidationAttribute : ActionFilterAttribute
{
    public AutomationSettingsValidationAttribute() => Order = -3000;
    public override void OnActionExecuting(ActionExecutingContext context)
    {
        if (context.ModelState.IsValid) return;
        var problem = new ProblemDetails { Status = 400, Title = "Automation settings request failed",
            Detail = "All five settings are required. Supply valid field types and limits: minute 1..100, day 1..10000, failures 1..20. Unknown fields are not accepted." };
        problem.Extensions["code"] = "InvalidRequest";
        context.Result = new BadRequestObjectResult(problem);
    }
}

public sealed class AutomationSettingsExceptionAttribute : ExceptionFilterAttribute
{
    public override void OnException(ExceptionContext context)
    {
        if (context.Exception is OperationCanceledException && context.HttpContext.RequestAborted.IsCancellationRequested) return;
        context.HttpContext.RequestServices.GetRequiredService<ILogger<AutomationSettingsExceptionAttribute>>()
            .LogError(context.Exception, "Automation settings request failed");
        var problem = new ProblemDetails { Status = 500, Title = "Automation settings request failed", Detail = "The request could not be completed." };
        problem.Extensions["code"] = "AutomationSettingsInternalError";
        context.Result = new ObjectResult(problem) { StatusCode = 500, ContentTypes = { "application/problem+json" } };
        context.ExceptionHandled = true;
    }
}

