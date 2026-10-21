using Microsoft.AspNetCore.Mvc;
using ProspectionCrm.Api.Dtos.CalendarEvents;
using ProspectionCrm.Api.Services;
using ProspectionCrm.Api.Services.Calendar;

namespace ProspectionCrm.Api.Controllers;

[ApiController, Route("api/calendar-accounts")]
public sealed class CalendarAccountsController(CalendarAccountService accounts, ICurrentWorkspaceProvider workspace) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken token) => Ok(await accounts.ListAsync(await workspace.GetCurrentWorkspaceIdAsync(token), token));

    [HttpPost]
    public async Task<IActionResult> Create(CreateCalendarAccountRequest request, CancellationToken token)
    {
        var result = await accounts.CreateAsync(await workspace.GetCurrentWorkspaceIdAsync(token), request, token);
        return Created("/api/calendar-accounts", result);
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, UpdateCalendarAccountRequest request, CancellationToken token) =>
        Ok(await accounts.UpdateAsync(await workspace.GetCurrentWorkspaceIdAsync(token), id, request, token));

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Read(Guid id, CancellationToken token)
    {
        var item = (await accounts.ListAsync(await workspace.GetCurrentWorkspaceIdAsync(token), token)).SingleOrDefault(x => x.Id == id);
        return item is null ? NotFound() : Ok(item);
    }
    [HttpPost("{id:guid}/calendars")]
    public async Task<IActionResult> CreateCalendar(Guid id, CreateCalendarRequest request, [FromServices] CalendarCatalogService catalog, CancellationToken token) =>
        Created("/api/calendars", await catalog.CreateAsync(await workspace.GetCurrentWorkspaceIdAsync(token), id, request, token));

}

[ApiController, Route("api/calendar-sync-jobs")]
public sealed class CalendarSyncJobsController(CalendarSyncService sync, CalendarSyncDispatcher dispatcher, ICurrentWorkspaceProvider workspace) : ControllerBase
{
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Read(Guid id, CancellationToken token)
    {
        var result = await sync.FindAsync(await workspace.GetCurrentWorkspaceIdAsync(token), id, token);
        return result is null ? NotFound() : Ok(result);
    }
    [HttpPost("{id:guid}/process-result")]
    public async Task<IActionResult> Process(Guid id, CancellationToken token)
    { await sync.ProcessAsync(await workspace.GetCurrentWorkspaceIdAsync(token), id, token); return NoContent(); }
    [HttpPost("{id:guid}/dispatch")]
    public async Task<IActionResult> Dispatch(Guid id, CancellationToken token)
    { await dispatcher.DispatchAsync(await workspace.GetCurrentWorkspaceIdAsync(token), id, token); return NoContent(); }
    [HttpPost("{id:guid}/resolve-unknown")]
    public async Task<IActionResult> Resolve(Guid id, CancellationToken token)
    { await sync.ResolveUnknownAsync(await workspace.GetCurrentWorkspaceIdAsync(token), id, token); return NoContent(); }
    [HttpPost("{id:guid}/cancel")]
    public async Task<IActionResult> Cancel(Guid id, CancellationToken token)
    { await sync.CancelAsync(await workspace.GetCurrentWorkspaceIdAsync(token), id, token); return NoContent(); }
}

[ApiController, Route("api/calendars")]
public sealed class CalendarsController(CalendarCatalogService catalog, CalendarSyncService sync, ICurrentWorkspaceProvider workspace) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken token) => Ok(await catalog.ListAsync(await workspace.GetCurrentWorkspaceIdAsync(token), token));
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Read(Guid id, CancellationToken token)
    {
        var item = (await catalog.ListAsync(await workspace.GetCurrentWorkspaceIdAsync(token), token)).SingleOrDefault(x => x.Id == id);
        return item is null ? NotFound() : Ok(item);
    }
    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, UpdateCalendarRequest request, CancellationToken token) =>
        Ok(await catalog.UpdateAsync(await workspace.GetCurrentWorkspaceIdAsync(token), id, request, token));
    [HttpPost("{id:guid}/sync-jobs")]
    public async Task<IActionResult> Enqueue(Guid id, EnqueueCalendarSyncRequest request, CancellationToken token)
    {
        var result = await sync.EnqueueAsync(await workspace.GetCurrentWorkspaceIdAsync(token), id, request.MaxItems, token);
        return Accepted($"/api/calendar-sync-jobs/{result.Id}", result);
    }
}
