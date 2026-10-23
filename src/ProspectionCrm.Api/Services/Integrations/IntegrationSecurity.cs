using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;
using ProspectionCrm.Api.Security;
using ProspectionCrm.Api.Dtos.Integrations;

namespace ProspectionCrm.Api.Services.Integrations;

public sealed record IntegrationAccess(Guid WorkspaceId, Guid ConnectionId);
public sealed class IntegrationKey
{
    public string Id { get; set; } = "";
    public string SecretBase64 { get; set; } = "";
    public bool Enabled { get; set; }
    public Guid ConnectionId { get; set; }
    public Guid WorkspaceId { get; set; }
    public bool IsValid()
    {
        try { return Regex.IsMatch(Id, "\\A[a-zA-Z0-9_-]{1,64}\\z") && Convert.FromBase64String(SecretBase64).Length == 32
            && ConnectionId != Guid.Empty && WorkspaceId != Guid.Empty; }
        catch (FormatException) { return false; }
    }
}

public sealed class IntegrationOptions
{
    public bool Enabled { get; set; }
    public EmailSyncTransportOptions EmailSync { get; set; } = new();
    public IntegrationDispatchLimits DispatchLimits { get; set; } = new();
    public EmailSyncTransportOptions CalendarSync { get; set; } = new();
    public EmailSyncTransportOptions CalendarEvent { get; set; } = new();
    public EmailSyncTransportOptions EmailSend { get; set; } = new();
    public string Endpoint { get; set; } = "";
    public Guid ConnectionId { get; set; }
    public Guid WorkspaceId { get; set; }
    public int TimeoutSeconds { get; set; } = 15;
    public int MaxConcurrentSends { get; set; } = 2;
    public int ClockWindowSeconds { get; set; } = 300;
    public IntegrationKey OutgoingKey { get; set; } = new();
    public List<IntegrationKey> IncomingKeys { get; set; } = [];
    public bool IsValid(OperatorOptions human) => DispatchLimits is not null && DispatchLimits.IsValid()
        && (!EmailSync.Enabled || Enabled && EmailSync.IsValid())
        && (!CalendarSync.Enabled || Enabled && CalendarSync.IsValid())
        && (!CalendarEvent.Enabled || Enabled && CalendarEvent.IsValid())
        && (!EmailSend.Enabled || Enabled && EmailSend.IsValid())
        && (!Enabled || (human.IsConfigured && human.WorkspaceId == WorkspaceId
        && ConnectionId != Guid.Empty && TimeoutSeconds is >= 1 and <= 60 && MaxConcurrentSends is >= 1 and <= 8
        && ClockWindowSeconds is >= 30 and <= 300
        && Uri.TryCreate(Endpoint, UriKind.Absolute, out var uri) && uri.Scheme == "https"
        && string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment)
        && OutgoingKey.Enabled && OutgoingKey.IsValid() && OutgoingKey.WorkspaceId == WorkspaceId && OutgoingKey.ConnectionId == ConnectionId
        && IncomingKeys.Count is >= 1 and <= 16 && IncomingKeys.All(k => k.IsValid())
        && IncomingKeys.Select(k => k.Id).Distinct(StringComparer.Ordinal).Count() == IncomingKeys.Count
        && IncomingKeys.All(k => !CryptographicOperations.FixedTimeEquals(Convert.FromBase64String(k.SecretBase64), Convert.FromBase64String(OutgoingKey.SecretBase64)))
        && IncomingKeys.Any(k => k.Enabled && k.ConnectionId == ConnectionId && k.WorkspaceId == WorkspaceId)));
}

public sealed class EmailSyncTransportOptions
{
    public bool Enabled { get; set; }
    public string Endpoint { get; set; } = "";
    public bool IsValid() => Uri.TryCreate(Endpoint, UriKind.Absolute, out var uri) && uri.Scheme == "https"
        && string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment);
}

public static class IntegrationSignature
{
    public const string ResultTarget = "/api/integrations/results";
    public const string IncomingAudience = "n8n-to-crm";
    public const string OutgoingAudience = "crm-to-n8n";
    public const string KeyHeader = "X-CRM-Key-Id";
    public const string TimeHeader = "X-CRM-Timestamp";
    public const string SignatureHeader = "X-CRM-Signature";
    public static string Sign(byte[] key, string audience, string keyId, string timestamp, string method, string target, byte[] body)
    {
        var digest = Convert.ToHexStringLower(SHA256.HashData(body));
        var input = $"crm-hmac-v1\n{audience}\n{keyId}\n{timestamp}\n{method}\n{target}\n{digest}";
        return Convert.ToHexStringLower(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(input)));
    }
}

public sealed class IntegrationAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> schemeOptions,
    ILoggerFactory logger, UrlEncoder encoder, IOptionsMonitor<IntegrationOptions> options, TimeProvider clock)
    : AuthenticationHandler<AuthenticationSchemeOptions>(schemeOptions, logger, encoder)
{
    public const string BodyItem = "integration-raw-body";
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var config = options.CurrentValue;
        if (!config.Enabled || !Request.IsHttps || Request.Method != "POST"
            || Request.HttpContext.Features.Get<IHttpRequestFeature>()?.RawTarget != IntegrationSignature.ResultTarget)
            return AuthenticateResult.Fail("invalid-integration-authentication");
        string? Single(string name) => Request.Headers.TryGetValue(name, out var values) && values.Count == 1 ? values[0] : null;
        var keyId = Single(IntegrationSignature.KeyHeader);
        var timestamp = Single(IntegrationSignature.TimeHeader);
        var signature = Single(IntegrationSignature.SignatureHeader);
        var key = config.IncomingKeys.SingleOrDefault(k => k.Enabled && k.Id == keyId);
        if (key is null || timestamp is null || !Regex.IsMatch(timestamp, "\\A[0-9]{10}\\z")
            || !long.TryParse(timestamp, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            || Math.Abs(clock.GetUtcNow().ToUnixTimeSeconds() - seconds) > config.ClockWindowSeconds
            || signature is null || !Regex.IsMatch(signature, "\\A[0-9a-f]{64}\\z"))
            return AuthenticateResult.Fail("invalid-integration-authentication");
        if (Request.Headers.ContainsKey("Content-Encoding")
            || !System.Net.Http.Headers.MediaTypeHeaderValue.TryParse(Request.ContentType, out var contentType)
            || contentType.MediaType != "application/json" || contentType.Parameters.Any(p => !p.Name.Equals("charset", StringComparison.OrdinalIgnoreCase))
            || contentType.Parameters.Count > 1 || (contentType.CharSet is not null && !contentType.CharSet.Equals("utf-8", StringComparison.OrdinalIgnoreCase)))
        { Context.Items["integration-auth-status"] = 415; return AuthenticateResult.Fail("unsupported-body"); }
        var bytes = new byte[CollectionIntegrationContracts.MaxResultBytes + 1];
        var size = 0;
        while (size < bytes.Length)
        {
            var read = await Request.Body.ReadAsync(bytes.AsMemory(size), Context.RequestAborted);
            if (read == 0) break;
            size += read;
        }
        if (size > CollectionIntegrationContracts.MaxResultBytes
            || size > IntegrationContracts.MaxEnvelopeBytes && !CollectionIntegrationContracts.IsCollection(bytes.AsMemory(0, size))
                && !EmailSyncContracts.IsEmailSync(bytes.AsMemory(0, size))
                && !CalendarSyncContracts.IsCalendarSync(bytes.AsMemory(0, size))
                && !(size <= CalendarEventContracts.MaxBytes && CalendarEventContracts.IsCalendarEvent(Encoding.UTF8.GetString(bytes, 0, size))))
        { Context.Items["integration-auth-status"] = 413; return AuthenticateResult.Fail("body-too-large"); }
        var body = bytes.AsSpan(0, size).ToArray();
        var expected = IntegrationSignature.Sign(Convert.FromBase64String(key.SecretBase64), IntegrationSignature.IncomingAudience,
            key.Id, timestamp, "POST", IntegrationSignature.ResultTarget, body);
        if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(expected), Convert.FromHexString(signature)))
            return AuthenticateResult.Fail("invalid-integration-authentication");
        Context.Items[BodyItem] = body;
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity([
            new("integration-connection", key.ConnectionId.ToString("D")),
            new(SecurityNames.Workspace, key.WorkspaceId.ToString("D"))], SecurityNames.Machine)), SecurityNames.Machine));
    }
    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    { Response.StatusCode = Context.Items["integration-auth-status"] as int? ?? 401; return Task.CompletedTask; }
}
