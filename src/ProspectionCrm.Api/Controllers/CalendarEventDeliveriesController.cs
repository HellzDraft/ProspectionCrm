using Microsoft.AspNetCore.Mvc;
using ProspectionCrm.Api.Services;
using ProspectionCrm.Api.Services.Calendar;

namespace ProspectionCrm.Api.Controllers;

[ApiController, Route("api/calendar-event-deliveries")]
public sealed class CalendarEventDeliveriesController(CalendarEventDeliveryService deliveries, CalendarEventDispatcher dispatcher,
    ICurrentWorkspaceProvider workspace) : ControllerBase
{
    [HttpPost("from-plan/{planId:guid}")]
    public async Task<IActionResult> Create(Guid planId, CancellationToken token) =>
        Ok(await deliveries.CreateAsync(await workspace.GetCurrentWorkspaceIdAsync(token), planId, token));
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken token) =>
        await deliveries.FindAsync(await workspace.GetCurrentWorkspaceIdAsync(token), id, token) is { } d ? Ok(d) : NotFound();
    [HttpPost("{id:guid}/dispatch")]
    public async Task<IActionResult> Dispatch(Guid id, CancellationToken token)
    { await dispatcher.DispatchAsync(await workspace.GetCurrentWorkspaceIdAsync(token), id, token); return NoContent(); }
    [HttpPost("{id:guid}/reconcile")]
    public async Task<IActionResult> Reconcile(Guid id, CancellationToken token)
    { await deliveries.ReconcileAsync(await workspace.GetCurrentWorkspaceIdAsync(token), id, token); return NoContent(); }
}
