using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ProspectionCrm.Api.Data;

namespace ProspectionCrm.Api.Security;

public sealed class OperatorOptions
{
    public Guid UserId { get; set; }
    public Guid WorkspaceId { get; set; }
    public string PasswordHash { get; set; } = "";
    public string[] AllowedOrigins { get; set; } = ["https://localhost:7075"];
    public bool IsConfigured => UserId != Guid.Empty && WorkspaceId != Guid.Empty && ValidHash();
    private bool ValidHash()
    {
        try
        {
            var bytes = Convert.FromBase64String(PasswordHash);
            // PasswordHasher Identity V3, bounded to the current supported format.
            return bytes.Length == 61 && bytes[0] == 1
                && System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(1, 4)) <= 2
                && System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(5, 4)) is >= 10000 and <= 1000000
                && System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(9, 4)) == 16;
        }
        catch (FormatException) { return false; }
    }
    public string Stamp => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{UserId:D}|{WorkspaceId:D}|{PasswordHash}")));
}

public static class SecurityNames
{
    public const string Human = "Operator";
    public const string Machine = "IntegrationHmac";
    public const string Workspace = "crm-workspace";
    public const string Stamp = "crm-credential-stamp";
}

public interface IHumanActor
{
    Task<(Guid UserId, Guid WorkspaceId)> RequireAsync(CancellationToken token = default);
}

// HTTP identity only. Internal workers continue to receive workspace IDs from durable jobs.
public sealed class HumanActor(IHttpContextAccessor http, ProspectionCrmDbContext db) : IHumanActor
{
    public async Task<(Guid UserId, Guid WorkspaceId)> RequireAsync(CancellationToken token = default)
    {
        var principal = http.HttpContext?.User;
        if (principal?.Identity?.IsAuthenticated != true || principal.Identity.AuthenticationType != SecurityNames.Human
            || !Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var user)
            || !Guid.TryParse(principal.FindFirstValue(SecurityNames.Workspace), out var workspace)
            || !await db.Workspaces.AnyAsync(x => x.Id == workspace && x.OwnerUserId == user && x.ArchivedAt == null
                && db.UserAccounts.Any(u => u.Id == user), token))
            throw new UnauthorizedAccessException("operator-required");
        return (user, workspace);
    }
}

public sealed class OperatorCookieEvents(IOptionsMonitor<OperatorOptions> options, ProspectionCrmDbContext db) : CookieAuthenticationEvents
{
    public override async Task ValidatePrincipal(CookieValidatePrincipalContext context)
    {
        var config = options.CurrentValue;
        if (!config.IsConfigured || context.Principal?.FindFirstValue(SecurityNames.Stamp) != config.Stamp
            || context.Principal.FindFirstValue(ClaimTypes.NameIdentifier) != config.UserId.ToString("D")
            || context.Principal.FindFirstValue(SecurityNames.Workspace) != config.WorkspaceId.ToString("D")
            || !await db.Workspaces.AnyAsync(x => x.Id == config.WorkspaceId && x.OwnerUserId == config.UserId
                && x.ArchivedAt == null && db.UserAccounts.Any(u => u.Id == config.UserId), context.HttpContext.RequestAborted))
            context.RejectPrincipal();
    }
    public override Task RedirectToLogin(RedirectContext<CookieAuthenticationOptions> context)
    { context.Response.StatusCode = 401; return Task.CompletedTask; }
    public override Task RedirectToAccessDenied(RedirectContext<CookieAuthenticationOptions> context)
    { context.Response.StatusCode = 403; return Task.CompletedTask; }
}
