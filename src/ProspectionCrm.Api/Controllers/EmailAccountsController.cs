using Microsoft.AspNetCore.Mvc;
using ProspectionCrm.Api.Dtos.EmailMessages;
using ProspectionCrm.Api.Services;
using ProspectionCrm.Api.Services.Email;

namespace ProspectionCrm.Api.Controllers;

[ApiController, Route("api/email-accounts")]
public sealed class EmailAccountsController(EmailAccountService accounts, EmailSyncService sync, ICurrentWorkspaceProvider workspace) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken token) => Ok(await accounts.ListAsync(await workspace.GetCurrentWorkspaceIdAsync(token), token));

    [HttpPost]
    public async Task<IActionResult> Create(CreateEmailAccountRequest request, CancellationToken token)
    {
        var result = await accounts.CreateAsync(await workspace.GetCurrentWorkspaceIdAsync(token), request, token);
        return Created("/api/email-accounts", result);
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, UpdateEmailAccountRequest request, CancellationToken token) =>
        Ok(await accounts.UpdateAsync(await workspace.GetCurrentWorkspaceIdAsync(token), id, request, token));

    [HttpPost("{id:guid}/sync-jobs")]
    public async Task<IActionResult> Enqueue(Guid id, EnqueueEmailSyncRequest request, CancellationToken token)
    {
        var result = await sync.EnqueueAsync(await workspace.GetCurrentWorkspaceIdAsync(token), id, request.MaxItems, token);
        return Accepted($"/api/email-sync-jobs/{result.Id}", result);
    }
}

[ApiController, Route("api/email-sync-jobs")]
public sealed class EmailSyncJobsController(EmailSyncService sync, EmailSyncDispatcher dispatcher, ICurrentWorkspaceProvider workspace) : ControllerBase
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
    [HttpPost("{id:guid}/cancel")]
    public async Task<IActionResult> Cancel(Guid id, CancellationToken token)
    { await sync.CancelAsync(await workspace.GetCurrentWorkspaceIdAsync(token), id, token); return NoContent(); }
}
