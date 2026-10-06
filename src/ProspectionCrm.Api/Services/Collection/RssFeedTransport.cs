using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Options;

namespace ProspectionCrm.Api.Services.Collection;

public sealed record RssFeedResponse(byte[] Content, Uri FinalUri);
public interface IRssFeedTransport
{
    Task<RssFeedResponse> FetchAsync(Uri uri, CancellationToken cancellationToken);
}

public sealed class RssFeedTransport : IRssFeedTransport, IDisposable
{
    private readonly HttpClient client;
    private readonly RssCollectionOptions options;
    public RssFeedTransport(IOptions<RssCollectionOptions> options, RssConnectionFactory connections)
        : this(options.Value, CreateHandler(connections)) { }

    // A handler seam permits deterministic transport tests; production always uses CreateHandler.
    public RssFeedTransport(RssCollectionOptions options, HttpMessageHandler handler)
    {
        this.options = options;
        client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }
    public static SocketsHttpHandler CreateHandler(RssConnectionFactory connections) => new()
    {
        AllowAutoRedirect = false, UseCookies = false, UseProxy = false, Credentials = null,
        AutomaticDecompression = DecompressionMethods.All,
        SslOptions = new()
        {
            // Certificate validation must not make independent AIA/CRL HTTP requests
            // outside the validated socket path. Keep system trust, hostname and server EKU checks.
            CertificateChainPolicy = new()
            {
                DisableCertificateDownloads = true, RevocationMode = X509RevocationMode.NoCheck,
                ApplicationPolicy = { new Oid("1.3.6.1.5.5.7.3.1") }
            }
        },
        MaxResponseHeadersLength = 32,
        // No pooled connection can bypass the per-attempt DNS policy.
        PooledConnectionLifetime = TimeSpan.Zero,
        ConnectCallback = (context, token) => connections.ConnectAsync(context.DnsEndPoint, token)
        // SocketsHttpHandler performs normal TLS certificate/hostname validation after this callback.
    };

    public async Task<RssFeedResponse> FetchAsync(Uri uri, CancellationToken cancellationToken)
    {
        var current = RssNetworkPolicy.ValidateUri(uri.AbsoluteUri);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));
        try
        {
            for (var redirects = 0; ; redirects++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, current)
                {
                    Version = HttpVersion.Version11, VersionPolicy = HttpVersionPolicy.RequestVersionExact
                };
                request.Headers.UserAgent.ParseAdd(options.UserAgent);
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or HttpStatusCode.SeeOther
                    or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
                {
                    if (redirects >= options.MaxRedirects) throw Error("TooManyRedirects", 502);
                    var location = response.Headers.Location;
                    if (location is null || !Uri.TryCreate(current, location, out var target)) throw Error("UnsafeRedirect", 502);
                    current = RssNetworkPolicy.ValidateUri(target.AbsoluteUri, "UnsafeRedirect");
                    continue;
                }
                if (!response.IsSuccessStatusCode)
                    throw new SourceCollectionException(new("UpstreamHttpError", 502, (int)response.StatusCode));
                if (response.Content.Headers.ContentLength > options.MaxResponseBytes) throw Error("ResponseTooLarge", 422);
                var media = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant();
                if (media is not (null or "application/rss+xml" or "application/atom+xml" or "application/xml"
                    or "text/xml" or "application/octet-stream")) throw Error("UnsupportedContentType", 422);
                await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
                using var buffer = new MemoryStream();
                var chunk = new byte[8192];
                int count;
                while ((count = await stream.ReadAsync(chunk, timeout.Token)) != 0)
                {
                    if (buffer.Length + count > options.MaxResponseBytes) throw Error("ResponseTooLarge", 422);
                    buffer.Write(chunk, 0, count);
                }
                var content = buffer.ToArray();
                if (media is null or "application/octet-stream")
                {
                    using var sniff = new StreamReader(new MemoryStream(content), detectEncodingFromByteOrderMarks: true);
                    int c;
                    do { c = sniff.Read(); } while (c >= 0 && char.IsWhiteSpace((char)c));
                    if (c != '<') throw Error("UnsupportedContentType", 422);
                }
                timeout.Token.ThrowIfCancellationRequested();
                return new(content, current);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw Error("UpstreamTimeout", 504); }
        catch (HttpRequestException exception)
        {
            for (Exception? nested = exception; nested is not null; nested = nested.InnerException)
                if (nested is SourceCollectionException controlled) throw new SourceCollectionException(controlled.Error);
            throw Error(exception.HttpRequestError == HttpRequestError.SecureConnectionError ? "UpstreamTlsFailure"
                : exception.HttpRequestError == HttpRequestError.NameResolutionError ? "DnsResolutionFailed" : "UpstreamTransportError", 502);
        }
        catch (IOException) { throw Error("UpstreamTransportError", 502); }
    }
    private static SourceCollectionException Error(string code, int status) => new(new(code, status));
    public void Dispose() => client.Dispose();
}
