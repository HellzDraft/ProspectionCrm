using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using ProspectionCrm.Api.Services.Integrations;
using ProspectionCrm.Api.Dtos.Integrations;
using ProspectionCrm.Api.Security;

namespace ProspectionCrm.Api.Controllers;

[ApiController, Authorize, Route("api/integration-supervision")]
[IntegrationSupervisionErrors]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class IntegrationSupervisionController(IntegrationSupervisionService service) : ControllerBase
{
    [HttpPost("operations/{id:guid}/recover")]
    [Authorize(AuthenticationSchemes = SecurityNames.Human)]
    [ValidateAntiForgeryToken]
    [RequestSizeLimit(16384)]
    public async Task<IActionResult> Recover(Guid id, IntegrationRecoveryRequest request,
        [FromServices] IntegrationRecoveryService recovery, CancellationToken token)
    {
        var c = request.Confirmation;
        if (request.Action != "process-recorded-evidence" || request.ExpectedRevision < 0 || c is null
            || c.OperationId != id || c.BusinessId == Guid.Empty || string.IsNullOrWhiteSpace(c.Type)
            || string.IsNullOrWhiteSpace(c.BusinessStatus) || c.Revision != request.ExpectedRevision
            || c.ReceiptIds is null || c.ReceiptIds.Count is < 1 or > 100
            || c.ReceiptIds.Any(x => x == Guid.Empty) || c.ReceiptIds.Distinct().Count() != c.ReceiptIds.Count)
            return IntegrationSupervisionErrorsAttribute.Failure(400, "InvalidRequest");
        var status = await recovery.RecoverAsync(id, request, token);
        return status switch
        {
            200 => Ok(new IntegrationRecoveryResponse(id, "recorded-evidence-processed", $"/api/integration-supervision/operations/{id}/diagnostic")),
            404 => NotFound(),
            _ => IntegrationSupervisionErrorsAttribute.Failure(409, "RecoveryRejected")
        };
    }

    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken token) => Ok(await service.SnapshotAsync(token));

    [HttpGet("operations/{id:guid}/diagnostic")]
    public async Task<IActionResult> Diagnostic(Guid id, CancellationToken token,
        int attemptOffset = 0, int receiptOffset = 0, int limit = 50)
    {
        if (attemptOffset is < 0 or > 1000000 || receiptOffset is < 0 or > 1000000 || limit is < 1 or > 100)
            return IntegrationSupervisionErrorsAttribute.Failure(400, "InvalidRequest");
        var result = await service.DiagnosticAsync(id, attemptOffset, receiptOffset, limit, token);
        return result is null ? NotFound() : Ok(result);
    }

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
