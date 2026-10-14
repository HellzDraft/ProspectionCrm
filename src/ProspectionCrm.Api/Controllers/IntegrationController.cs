using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ProspectionCrm.Api.Data;
using ProspectionCrm.Api.Dtos.Integrations;
using ProspectionCrm.Api.Security;
using ProspectionCrm.Api.Services;
using ProspectionCrm.Api.Services.Integrations;

namespace ProspectionCrm.Api.Controllers;

[ApiController, Route("api/integrations")]
public sealed class IntegrationController(IntegrationStore store, IntegrationProbeService probes,
    ICurrentWorkspaceProvider workspaceProvider, ProspectionCrmDbContext db) : ControllerBase
{
    [HttpPost("results"), IgnoreAntiforgeryToken]
    [Authorize(Policy = SecurityNames.Machine, AuthenticationSchemes = SecurityNames.Machine)]
    public async Task<IActionResult> Receive(CancellationToken token)
    {
        var raw = (byte[])HttpContext.Items[IntegrationAuthenticationHandler.BodyItem]!;
        var access = new IntegrationAccess(Guid.Parse(User.FindFirstValue(SecurityNames.Workspace)!),
            Guid.Parse(User.FindFirstValue("integration-connection")!));
        try
        {
            var json = new UTF8Encoding(false, true).GetString(raw);
            var result = await store.ReceiveAsync(access, json, token);
            return Accepted(new { status = "received", alreadyRecorded = result == IntegrationReception.AlreadyRecorded });
        }
        catch (DecoderFallbackException) { return BadRequest(new { error = "invalid-integration-envelope" }); }
    }

    [HttpPost("probes")]
    public async Task<IActionResult> Create(CancellationToken token)
    {
        var workspace = await workspaceProvider.GetCurrentWorkspaceIdAsync(token);
        var config = probes.RequireConfiguration(workspace);
        var op = await store.CreateProbeAsync(workspace, config.ConnectionId, token);
        return Created($"/api/integrations/probes/{op.Id}", op);
    }

    [HttpGet("probes/{id:guid}")]
    public async Task<IActionResult> Read(Guid id, CancellationToken token)
    {
        var workspace = await workspaceProvider.GetCurrentWorkspaceIdAsync(token);
        var op = await store.FindAsync(workspace, id, token);
        if (op is null) return NotFound();
        return Ok(new { operation = op, attempts = await db.IntegrationAttempts.AsNoTracking().Where(x => x.OperationId == id)
            .OrderBy(x => x.Number).ToListAsync(token), receipt = await db.IntegrationReceipts.AsNoTracking().Where(x => x.OperationId == id)
            .Select(x => new { x.MessageId, x.ReceivedAt, x.ProcessedAt }).SingleOrDefaultAsync(token) });
    }

    [HttpGet("pending-receipts")]
    public async Task<IActionResult> Pending(CancellationToken token) => Ok(await store.PendingReceiptsAsync(
        await workspaceProvider.GetCurrentWorkspaceIdAsync(token), token: token));

    [HttpPost("probes/{id:guid}/send")]
    public async Task<IActionResult> Send(Guid id, RevisionRequest request, CancellationToken token) => Ok(new
    { transport = (await probes.SendAsync(await workspaceProvider.GetCurrentWorkspaceIdAsync(token), id, request.Revision, token)).ToString().ToLowerInvariant() });

    [HttpPost("probes/{id:guid}/process")]
    public async Task<IActionResult> Process(Guid id, RevisionRequest request, CancellationToken token)
    { await store.ProcessProbeAsync(await workspaceProvider.GetCurrentWorkspaceIdAsync(token), id, request.Revision, token); return NoContent(); }

    [HttpPost("probes/{id:guid}/attempts/{attemptId:guid}/resolve-unknown")]
    public async Task<IActionResult> Resolve(Guid id, Guid attemptId, CancellationToken token)
    { await probes.ResolveAsync(await workspaceProvider.GetCurrentWorkspaceIdAsync(token), id, attemptId, token); return NoContent(); }

    public sealed record RevisionRequest(long Revision);
}
