using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.WebAssembly.Http;

namespace ProspectionCrm.Blazor.Services;

public sealed class SessionState
{
    public bool Authenticated { get; set; }
    public event Action? Changed;
    public void Set(bool value) { Authenticated = value; Changed?.Invoke(); }
}

public sealed class ApiCookieHandler(Uri api, SessionState state, NavigationManager navigation) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        // Refuse arbitrary absolute URLs before credentials or CSRF material can be attached.
        if (request.RequestUri is null || !api.IsBaseOf(request.RequestUri) || request.RequestUri.Scheme != "https"
            || request.RequestUri.Authority != api.Authority)
            throw new InvalidOperationException("Destination API non autorisée.");
        request.SetBrowserRequestCredentials(BrowserRequestCredentials.Include);
        request.SetBrowserRequestOption("redirect", "error");
        if (request.Method != HttpMethod.Get && request.Method != HttpMethod.Head && request.Method != HttpMethod.Options)
        {
            // Fetch for the current identity, including before login and after identity changes.
            using var csrf = new HttpRequestMessage(HttpMethod.Get, new Uri(api, "api/session/antiforgery"));
            csrf.SetBrowserRequestCredentials(BrowserRequestCredentials.Include);
            csrf.SetBrowserRequestOption("redirect", "error");
            using var response = await base.SendAsync(csrf, token);
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadFromJsonAsync<CsrfResponse>(token);
            request.Headers.Add("X-CSRF-TOKEN", body!.Token);
        }
        var result = await base.SendAsync(request, token);
        if (result.StatusCode == HttpStatusCode.Unauthorized)
        {
            state.Set(false);
            if (!request.RequestUri.AbsolutePath.StartsWith("/api/session", StringComparison.Ordinal))
                navigation.NavigateTo("login");
        }
        return result;
    }
    private sealed record CsrfResponse(string Token);
}

public sealed class SessionService(HttpClient client, SessionState state)
{
    public async Task RefreshAsync()
    { using var result = await client.GetAsync("api/session"); state.Set(result.IsSuccessStatusCode); }
    public async Task<bool> LoginAsync(string password)
    {
        using var result = await client.PostAsJsonAsync("api/session/login", new { password });
        state.Set(result.IsSuccessStatusCode); return result.IsSuccessStatusCode;
    }
    public async Task LogoutAsync()
    { using var result = await client.PostAsync("api/session/logout", null); result.EnsureSuccessStatusCode(); state.Set(false); }
}
