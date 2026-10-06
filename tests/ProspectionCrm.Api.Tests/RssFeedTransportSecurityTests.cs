using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Options;
using ProspectionCrm.Api.Services.Collection;
using Xunit;

namespace ProspectionCrm.Api.Tests;

public sealed class RssFeedTransportSecurityTests
{
    [Theory]
    [InlineData("http://example.org/feed")][InlineData("ftp://example.org/feed")][InlineData("/feed")]
    [InlineData("https://user:password@example.org/feed")][InlineData("https://example.org:444/feed")]
    [InlineData("https://localhost/feed")][InlineData("https://sub.localhost/feed")][InlineData("https://test.local/feed")]
    [InlineData("https://test.local./feed")][InlineData("https://8.8.8.8/feed")][InlineData("https://127.0.0.1/feed")]
    [InlineData("https://[::1]/feed")][InlineData("https://2130706433/feed")][InlineData("https://0x7f000001/feed")]
    [InlineData("https://example.org\\@localhost/feed")][InlineData("https://example.org/with space")]
    public void RejectsUnsafeUris(string uri) => Assert.Equal("UnsafeFeedUrl", Assert.Throws<SourceCollectionException>(() => RssNetworkPolicy.ValidateUri(uri)).Error.Code);

    [Fact]
    public void AllowsHttpsDnsAndRemovesFragment()
    {
        Assert.Equal("https://feeds.example.org/path?q=public", RssNetworkPolicy.ValidateUri("https://feeds.example.org:443/path?q=public#fragment").AbsoluteUri);
    }

    [Theory]
    [InlineData("10.0.0.1")][InlineData("127.0.0.1")][InlineData("169.254.169.254")][InlineData("172.16.0.1")]
    [InlineData("192.168.0.1")][InlineData("100.64.0.1")][InlineData("224.0.0.1")][InlineData("0.0.0.0")]
    [InlineData("255.255.255.255")][InlineData("192.0.2.1")][InlineData("198.51.100.1")][InlineData("203.0.113.1")]
    [InlineData("198.18.0.1")][InlineData("240.0.0.1")][InlineData("192.0.0.8")][InlineData("192.88.99.2")]
    [InlineData("fe80::1")][InlineData("fc00::1")][InlineData("fd00::1")][InlineData("::ffff:10.0.0.1")]
    [InlineData("::ffff:127.0.0.1")][InlineData("::")][InlineData("::1")][InlineData("ff02::1")]
    [InlineData("2001:db8::1")][InlineData("2002:a00:1::1")][InlineData("2001::1")][InlineData("3fff::1")]
    [InlineData("64:ff9b::a00:1")][InlineData("5f00::1")][InlineData("100::1")]
    public async Task RejectsForbiddenDnsWithoutAnySocketAttempt(string address)
    {
        var connector = new FakeConnector();
        var connections = new RssConnectionFactory(new FakeDns([IPAddress.Parse(address)]), connector);
        var error = await Assert.ThrowsAsync<SourceCollectionException>(async () => await connections.ConnectAsync(new("feeds.example.org", 443), default));
        Assert.Equal("UnsafeResolvedAddress", error.Error.Code); Assert.Empty(connector.Addresses);
    }

    [Fact]
    public async Task MixedAnswersRejectEntireHostAndEmptyOrFailedDnsAreControlled()
    {
        var connector = new FakeConnector();
        var mixed = new RssConnectionFactory(new FakeDns([IPAddress.Parse("8.8.8.8"), IPAddress.Parse("10.0.0.1")]), connector);
        Assert.Equal("UnsafeResolvedAddress", (await Assert.ThrowsAsync<SourceCollectionException>(async () => await mixed.ConnectAsync(new("feeds.example.org", 443), default))).Error.Code);
        Assert.Empty(connector.Addresses);
        foreach (var dns in new[] { new FakeDns([]), new FakeDns([], true) })
        {
            var connections = new RssConnectionFactory(dns, connector);
            Assert.Equal("DnsResolutionFailed", (await Assert.ThrowsAsync<SourceCollectionException>(async () => await connections.ConnectAsync(new("feeds.example.org", 443), default))).Error.Code);
        }
    }

    [Fact]
    public async Task ConnectsOnlyResolvedPublicAddressesAndRechecksEachConnection()
    {
        var dns = new FakeDns([IPAddress.Parse("8.8.8.8"), IPAddress.Parse("1.1.1.1")]);
        var connector = new FakeConnector();
        var connections = new RssConnectionFactory(dns, connector);
        await using var stream = await connections.ConnectAsync(new("feeds.example.org", 443), default);
        Assert.Equal(IPAddress.Parse("1.1.1.1"), Assert.Single(connector.Addresses));
        dns.Addresses = [IPAddress.Parse("127.0.0.1")];
        await Assert.ThrowsAsync<SourceCollectionException>(async () => await connections.ConnectAsync(new("feeds.example.org", 443), default));
        Assert.Single(connector.Addresses); Assert.Equal(2, dns.Calls);
        Assert.True(RssNetworkPolicy.IsPublic(IPAddress.Parse("2606:4700:4700::1111")));
        Assert.True(RssNetworkPolicy.IsPublic(IPAddress.Parse("::ffff:8.8.8.8")));
    }

    [Fact]
    public void ProductionHandlerDisablesAmbientCredentialsCookiesProxyRedirectsAndPooling()
    {
        using var handler = RssFeedTransport.CreateHandler(new(new FakeDns([]), new FakeConnector()));
        Assert.False(handler.AllowAutoRedirect); Assert.False(handler.UseProxy); Assert.False(handler.UseCookies);
        Assert.Null(handler.Credentials); Assert.Null(handler.Proxy); Assert.NotNull(handler.ConnectCallback);
        Assert.Equal(DecompressionMethods.All, handler.AutomaticDecompression);
        Assert.Equal(TimeSpan.Zero, handler.PooledConnectionLifetime);
        Assert.Null(handler.SslOptions.RemoteCertificateValidationCallback);
        var chain = handler.SslOptions.CertificateChainPolicy!;
        Assert.True(chain.DisableCertificateDownloads);
        Assert.Equal(System.Security.Cryptography.X509Certificates.X509RevocationMode.NoCheck, chain.RevocationMode);
        Assert.Equal(System.Security.Cryptography.X509Certificates.X509ChainTrustMode.System, chain.TrustMode);
        Assert.Equal(System.Security.Cryptography.X509Certificates.X509VerificationFlags.NoFlag, chain.VerificationFlags);
        Assert.Equal("1.3.6.1.5.5.7.3.1", Assert.Single(chain.ApplicationPolicy.Cast<System.Security.Cryptography.Oid>()).Value);
    }

    [Fact]
    public async Task FollowsPublicRedirectManuallyWithoutSensitiveHeaders()
    {
        var calls = new List<string>();
        using var transport = new RssFeedTransport(new(), new Handler((request, _) =>
        {
            calls.Add(request.RequestUri!.AbsoluteUri);
            Assert.Null(request.Headers.Authorization); Assert.False(request.Headers.Contains("Cookie"));
            Assert.Equal("ProspectionCrm/1.0", request.Headers.UserAgent.ToString());
            Assert.Equal(HttpVersion.Version11, request.Version);
            if (calls.Count == 1) return Task.FromResult(Redirect("https://other.example.org/final#ignored"));
            return Task.FromResult(Response("<rss/>", "application/rss+xml"));
        }));
        var result = await transport.FetchAsync(new("https://feeds.example.org/feed"), default);
        Assert.Equal("https://other.example.org/final", result.FinalUri.AbsoluteUri);
        Assert.Equal(2, calls.Count);
    }

    [Theory]
    [InlineData("http://example.org/feed")][InlineData("https://127.0.0.1/feed")]
    [InlineData("https://local.local/feed")][InlineData("https://user:pass@example.org/feed")]
    public async Task RejectsUnsafeRedirectWithoutSecondRequest(string target)
    {
        var calls = 0;
        using var transport = new RssFeedTransport(new(), new Handler((_, _) => { calls++; return Task.FromResult(Redirect(target)); }));
        Assert.Equal("UnsafeRedirect", (await Error(transport)).Code); Assert.Equal(1, calls);
    }

    [Fact]
    public async Task CapsRedirectsAndHandlesMissingLocation()
    {
        var calls = 0;
        using var transport = new RssFeedTransport(new(), new Handler((_, _) => { calls++; return Task.FromResult(Redirect("/next")); }));
        Assert.Equal("TooManyRedirects", (await Error(transport)).Code); Assert.Equal(4, calls);
        using var missing = new RssFeedTransport(new(), new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Redirect))));
        Assert.Equal("UnsafeRedirect", (await Error(missing)).Code);
    }

    [Theory]
    [InlineData("application/rss+xml")][InlineData("application/atom+xml")][InlineData("application/xml")]
    [InlineData("text/xml")][InlineData("application/octet-stream")][InlineData(null)]
    public async Task AcceptsXmlWithSupportedOrMissingContentType(string? type)
    {
        using var transport = new RssFeedTransport(new(), new Handler((_, _) => Task.FromResult(Response("\uFEFF  \n<rss/>", type))));
        Assert.NotEmpty((await transport.FetchAsync(new("https://feeds.example.org/feed"), default)).Content);
    }

    [Theory]
    [InlineData("text/html", "<html/>")][InlineData("application/json", "{}")]
    [InlineData(null, "not XML")][InlineData("application/octet-stream", "not XML")]
    public async Task RejectsUnsupportedContent(string? type, string body)
    {
        using var transport = new RssFeedTransport(new(), new Handler((_, _) => Task.FromResult(Response(body, type))));
        Assert.Equal("UnsupportedContentType", (await Error(transport)).Code);
    }

    [Theory]
    [InlineData(true)][InlineData(false)]
    public async Task EnforcesDeclaredAndStreamSize(bool knownLength)
    {
        using var transport = new RssFeedTransport(new() { MaxResponseBytes = 1024 }, new Handler((_, _) =>
        {
            var response = Response(new string('x', 1025), "application/xml");
            if (!knownLength) response.Content = new StreamContent(new NonSeekableStream(new byte[1025]));
            return Task.FromResult(response);
        }));
        Assert.Equal("ResponseTooLarge", (await Error(transport)).Code);
    }

    [Theory]
    [InlineData(404)][InlineData(500)]
    public async Task UpstreamStatusExposesOnlyNumericCode(int status)
    {
        using var transport = new RssFeedTransport(new(), new Handler((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)
        { Content = new StringContent("private upstream body") })));
        var error = await Error(transport);
        Assert.Equal("UpstreamHttpError", error.Code); Assert.Equal(status, error.UpstreamStatusCode);
        Assert.DoesNotContain("private", error.Detail);
    }

    [Fact]
    public async Task TimeoutAndClientCancellationAreDifferent()
    {
        using var transport = new RssFeedTransport(new() { TimeoutSeconds = 1 }, new Handler(async (_, token) =>
        { await Task.Delay(Timeout.Infinite, token); return Response("", null); }));
        Assert.Equal("UpstreamTimeout", (await Error(transport)).Code);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => transport.FetchAsync(new("https://feeds.example.org/feed"), cancelled.Token));
    }

    [Theory]
    [InlineData(HttpRequestError.SecureConnectionError, "UpstreamTlsFailure")]
    [InlineData(HttpRequestError.NameResolutionError, "DnsResolutionFailed")]
    [InlineData(HttpRequestError.ConnectionError, "UpstreamTransportError")]
    public async Task SanitizesTransportExceptions(HttpRequestError type, string code)
    {
        using var transport = new RssFeedTransport(new(), new Handler((_, _) => throw new HttpRequestException(type, "sensitive network details")));
        Assert.Equal(code, (await Error(transport)).Code);
    }

    [Fact]
    public async Task PreservesControlledConnectCallbackErrorThroughHttpClientWrapping()
    {
        using var transport = new RssFeedTransport(new(), new Handler((_, _) =>
            throw new HttpRequestException("sensitive host", new SourceCollectionException(new("UnsafeResolvedAddress", 502)))));
        Assert.Equal("UnsafeResolvedAddress", (await Error(transport)).Code);
    }

    [Fact]
    public void OptionsAndRegistryAreStrict()
    {
        Assert.True(new RssCollectionOptions().IsValid());
        Assert.False(new RssCollectionOptions { MaxRedirects = 4 }.IsValid());
        Assert.False(new RssCollectionOptions { TimeoutSeconds = 0 }.IsValid());
        Assert.False(new RssCollectionOptions { MaxResponseBytes = 2_000_001 }.IsValid());
        Assert.False(new RssCollectionOptions { DefaultMaxItems = 101 }.IsValid());
        Assert.False(new RssCollectionOptions { UserAgent = "secret" }.IsValid());
        var adapter = new RssAtomSourceAdapter(new NeverTransport(), new(), Options.Create(new RssCollectionOptions()));
        var registry = new SourceAdapterRegistry([adapter]);
        Assert.Same(adapter, registry.Find("rss")); Assert.Null(registry.Find("RSS"));
        Assert.Throws<InvalidOperationException>(() => new SourceAdapterRegistry([adapter, adapter]));
    }

    private static async Task<SourceAdapterError> Error(RssFeedTransport transport) =>
        (await Assert.ThrowsAsync<SourceCollectionException>(() => transport.FetchAsync(new("https://feeds.example.org/feed"), default))).Error;
    private static HttpResponseMessage Redirect(string location) => new(HttpStatusCode.Redirect) { Headers = { Location = new Uri(location, UriKind.RelativeOrAbsolute) } };
    private static HttpResponseMessage Response(string body, string? type)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body)) };
        if (type is not null) response.Content.Headers.ContentType = new MediaTypeHeaderValue(type);
        return response;
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
    private sealed class FakeDns(IPAddress[] addresses, bool fail = false) : IRssDnsResolver
    {
        public IPAddress[] Addresses { get; set; } = addresses;
        public int Calls { get; private set; }
        public Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken)
        { Calls++; if (fail) throw new SocketException(); return Task.FromResult(Addresses); }
    }
    private sealed class FakeConnector : IRssSocketConnector
    {
        public List<IPAddress> Addresses { get; } = [];
        public ValueTask<Stream> ConnectAsync(IPAddress address, int port, CancellationToken cancellationToken)
        { Assert.Equal(443, port); Addresses.Add(address); return ValueTask.FromResult<Stream>(new MemoryStream()); }
    }
    private sealed class NonSeekableStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }
    private sealed class NeverTransport : IRssFeedTransport
    {
        public Task<RssFeedResponse> FetchAsync(Uri uri, CancellationToken cancellationToken) => throw new InvalidOperationException("No network permitted.");
    }
}
