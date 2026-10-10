using Microsoft.AspNetCore.Mvc;
using ProspectionCrm.Api.Dtos.AutomationSupervision;
using ProspectionCrm.Api.Services.Automation;

namespace ProspectionCrm.Api.Controllers;

[ApiController]
[Route("api/automation-supervision")]
[AutomationSupervisionErrors("AutomationSupervisionInternalError")]
public sealed class AutomationSupervisionController(AutomationSupervisionService service) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<AutomationSupervisionDto>> Get(CancellationToken token)
    {
        var result = await service.ReadAsync(token);
        return result.Value is not null ? Ok(result.Value) : Problem(statusCode: result.Status,
            title: "Automation supervision unavailable", extensions: new Dictionary<string, object?> { ["code"] = result.Code });
    }
}
