extern alias Blazor;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Components;
using Blazor::ProspectionCrm.Blazor.Services;
using Xunit;

namespace ProspectionCrm.Api.Tests;

public sealed class BlazorSessionSecurityTests
{
    [Fact]
    public async Task WritesFetchFreshCsrfAndIncludeCredentialsOnlyForConfiguredApiWithoutRedirects()
    {
        var paths = new List<string>(); var tokens = new List<string>(); var number = 0;
        using var client = Client(new((request, _) =>
        {
            paths.Add(request.RequestUri!.AbsolutePath);
            var options = request.Options.Single(x => x.Key == "WebAssemblyFetchOptions").Value;
            var values = Assert.IsAssignableFrom<IDictionary<string, object>>(options);
            Assert.Equal("include", values["credentials"]); Assert.Equal("error", values["redirect"]);
            if (request.Method == HttpMethod.Get) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = JsonContent.Create(new { token = $"test-csrf-{++number}" }) });
            tokens.Add(Assert.Single(request.Headers.GetValues("X-CSRF-TOKEN")));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        }));
        await client.PostAsJsonAsync("api/session/login", new { password = "synthetic-password" });
        await client.PostAsync("api/session/logout", null);
        Assert.Equal(new[] { "/api/session/antiforgery", "/api/session/login", "/api/session/antiforgery", "/api/session/logout" }, paths);
        Assert.Equal(new[] { "test-csrf-1", "test-csrf-2" }, tokens);
    }

    [Theory]
    [InlineData("https://evil.example/api/companies")]
    [InlineData("https://localhost:9999/api/companies")]
    [InlineData("https://127.0.0.1:7096/api/companies")]
    [InlineData("http://localhost:7096/api/companies")]
    public async Task ForeignDestinationsAreRefusedBeforeAnyCsrfOrApiCall(string destination)
    {
        var calls = 0;
        using var client = Client(new((_, _) => { calls++; throw new InvalidOperationException("unexpected-network"); }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.PostAsync(destination, null)); Assert.Equal(0, calls);
    }

    [Fact]
    public async Task UnauthorizedCrmResponseUpdatesConnectionStateAndNavigatesToLogin()
    {
        var state = new SessionState { Authenticated = true }; var navigation = new Navigation();
        using var client = Client(new((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized))), state, navigation);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("api/companies")).StatusCode);
        Assert.False(state.Authenticated); Assert.Equal("login", navigation.Target);
    }
    private static HttpClient Client(Handler inner, SessionState? state = null, Navigation? navigation = null)
    {
        var api = new Uri("https://localhost:7096/");
        return new(new ApiCookieHandler(api, state ?? new(), navigation ?? new()) { InnerHandler = inner }) { BaseAddress = api };
    }
    private sealed class Navigation : NavigationManager
    {
        public string? Target;
        public Navigation() => Initialize("https://localhost:7075/", "https://localhost:7075/");
        protected override void NavigateToCore(string uri, bool forceLoad) => Target = uri;
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token); }
}
