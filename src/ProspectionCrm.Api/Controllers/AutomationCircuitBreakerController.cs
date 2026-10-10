using Microsoft.AspNetCore.Mvc;
using ProspectionCrm.Api.Dtos.AutomationSupervision;
using ProspectionCrm.Api.Services.Automation;

namespace ProspectionCrm.Api.Controllers;

[ApiController]
[Route("api/automation-circuit-breaker")]
[AutomationSupervisionErrors("AutomationCircuitBreakerInternalError")]
public sealed class AutomationCircuitBreakerController(AutomationSupervisionWorkspace workspace,
    AutomationCircuitBreakerService circuit) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<AutomationCircuitStatus>> Get(CancellationToken token)
    {
        var resolved = await workspace.ResolveAsync(token);
        return resolved.Value is { } settings ? Ok(await circuit.ReadAsync(settings, token))
            : Failure(resolved.Status, resolved.Code);
    }

    [HttpGet("resets")]
    public async Task<ActionResult<AutomationCircuitResetsPage>> List(CancellationToken token, int offset = 0, int limit = 50)
    {
        if (offset < 0 || limit is < 1 or > 200) return Failure(400, "InvalidPagination");
        var resolved = await workspace.ResolveAsync(token);
        if (resolved.Value is not { } settings) return Failure(resolved.Status, resolved.Code);
        var result = await circuit.ListAsync(settings.WorkspaceId, offset, limit, token);
        return result.Value is not null ? Ok(result.Value) : Failure(result.Status, result.Code);
    }

    [HttpPost("reset")]
    [RequestSizeLimit(16384)]
    public async Task<ActionResult<AutomationCircuitResetResult>> Reset(ResetAutomationCircuitRequest request, CancellationToken token)
    {
        var resolved = await workspace.ResolveAsync(token);
        if (resolved.Value is not { } settings) return Failure(resolved.Status, resolved.Code);
        var result = await circuit.ResetAsync(settings.WorkspaceId, request, token);
        return result.Value is not null ? Ok(result.Value) : Failure(result.Status, result.Code);
    }

    private ObjectResult Failure(int status, string? code) => Problem(statusCode: status,
        title: "Automation circuit request failed", extensions: new Dictionary<string, object?> { ["code"] = code });
}
