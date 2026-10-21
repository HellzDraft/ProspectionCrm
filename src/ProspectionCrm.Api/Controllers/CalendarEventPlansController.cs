using Microsoft.AspNetCore.Mvc;
using ProspectionCrm.Api.Dtos.CalendarEvents;
using ProspectionCrm.Api.Services;
using ProspectionCrm.Api.Services.Calendar;

namespace ProspectionCrm.Api.Controllers;

[ApiController, Route("api/calendar-event-plans"), RequestSizeLimit(65536)]
[AutomationActionRequestErrors]
public sealed class CalendarEventPlansController(CalendarEventPlanService service, ICurrentWorkspaceProvider workspace) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Create(CalendarEventContent content, CancellationToken token)
    {
        var plan = await service.CreateAsync(await workspace.GetCurrentWorkspaceIdAsync(token), content, token);
        return Created($"/api/calendar-event-plans/{plan.Id}", plan);
    }
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken token, int offset = 0, int limit = 50) =>
        Ok(await service.ListAsync(await workspace.GetCurrentWorkspaceIdAsync(token), offset, limit, token));
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken token) =>
        await service.GetAsync(await workspace.GetCurrentWorkspaceIdAsync(token), id, token) is { } plan ? Ok(plan) : NotFound();
    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, UpdateCalendarEventPlanRequest content, CancellationToken token) =>
        Ok(await service.UpdateAsync(await workspace.GetCurrentWorkspaceIdAsync(token), id, content, token));
    [HttpPost("{id:guid}/submit")]
    public async Task<IActionResult> Submit(Guid id, CalendarEventPlanRevision body, CancellationToken token) =>
        Ok(await service.SubmitAsync(await workspace.GetCurrentWorkspaceIdAsync(token), id, body.Revision, token));
    [HttpPost("{id:guid}/cancel")]
    public async Task<IActionResult> Cancel(Guid id, CalendarEventPlanRevision body, CancellationToken token) =>
        Ok(await service.CancelAsync(await workspace.GetCurrentWorkspaceIdAsync(token), id, body.Revision, token));
}
