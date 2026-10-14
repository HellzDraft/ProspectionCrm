using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ProspectionCrm.Api.Data;
using ProspectionCrm.Api.Security;

namespace ProspectionCrm.Api.Controllers;

[ApiController, Route("api/session")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class SessionController(IOptionsMonitor<OperatorOptions> options, ProspectionCrmDbContext db,
    IAntiforgery antiforgery, IHumanActor actor, TimeProvider clock) : ControllerBase
{
    [AllowAnonymous, HttpGet("antiforgery")]
    public IActionResult Antiforgery() => Ok(new { token = antiforgery.GetAndStoreTokens(HttpContext).RequestToken });

    [AllowAnonymous, HttpPost("login"), EnableRateLimiting("login"), RequestSizeLimit(4096)]
    public async Task<IActionResult> Login(LoginRequest request, CancellationToken token)
    {
        var config = options.CurrentValue;
        if (!config.IsConfigured || request.Password is not { Length: >= 12 and <= 256 }
            || new PasswordHasher<OperatorOptions>().VerifyHashedPassword(config, config.PasswordHash, request.Password)
                == PasswordVerificationResult.Failed
            || !await db.Workspaces.AnyAsync(x => x.Id == config.WorkspaceId && x.OwnerUserId == config.UserId
                && x.ArchivedAt == null && db.UserAccounts.Any(u => u.Id == config.UserId), token))
            return Unauthorized(new { error = "invalid-credentials" });

        var identity = new ClaimsIdentity([
            new(ClaimTypes.NameIdentifier, config.UserId.ToString("D")),
            new(SecurityNames.Workspace, config.WorkspaceId.ToString("D")),
            new(SecurityNames.Stamp, config.Stamp)], SecurityNames.Human);
        await HttpContext.SignInAsync(SecurityNames.Human, new ClaimsPrincipal(identity), new AuthenticationProperties
        { IsPersistent = false, AllowRefresh = false, IssuedUtc = clock.GetUtcNow(), ExpiresUtc = clock.GetUtcNow().AddHours(8) });
        return NoContent();
    }

    [HttpGet]
    public async Task<IActionResult> Session(CancellationToken token)
    { var current = await actor.RequireAsync(token); return Ok(new { current.UserId, current.WorkspaceId }); }

    [HttpPost("logout")]
    public async Task<IActionResult> Logout()
    { await HttpContext.SignOutAsync(SecurityNames.Human); return NoContent(); }

    public sealed record LoginRequest(string? Password);
}
