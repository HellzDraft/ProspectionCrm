using System.Net;
using System.Net.Sockets;

namespace ProspectionCrm.Api.Services.Collection;

public static class RssNetworkPolicy
{
    public static Uri ValidateUri(string? value, string code = "UnsafeFeedUrl")
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c == '\\')
            || !Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps || uri.Port != 443 || uri.UserInfo.Length != 0
            || uri.HostNameType != UriHostNameType.Dns)
            throw new SourceCollectionException(new(code, code == "UnsafeFeedUrl" ? 409 : 502));
        var host = uri.IdnHost.TrimEnd('.').ToLowerInvariant();
        if (!host.Contains('.') || host == "localhost" || host.EndsWith(".localhost", StringComparison.Ordinal)
            || host.EndsWith(".local", StringComparison.Ordinal) || host.EndsWith(".internal", StringComparison.Ordinal)
            || host == "home.arpa" || host.EndsWith(".home.arpa", StringComparison.Ordinal)
            || IPAddress.TryParse(host, out _))
            throw new SourceCollectionException(new(code, code == "UnsafeFeedUrl" ? 409 : 502));
        return new UriBuilder(uri) { Fragment = "" }.Uri;
    }

    // Conservative IANA special-purpose policy: entire reserved blocks are excluded,
    // including the few globally reachable anycast exceptions within those blocks.
    private static readonly IPNetwork[] V4Excluded = [
        IPNetwork.Parse("0.0.0.0/8"), IPNetwork.Parse("10.0.0.0/8"), IPNetwork.Parse("100.64.0.0/10"),
        IPNetwork.Parse("127.0.0.0/8"), IPNetwork.Parse("169.254.0.0/16"), IPNetwork.Parse("172.16.0.0/12"),
        IPNetwork.Parse("192.0.0.0/24"), IPNetwork.Parse("192.0.2.0/24"), IPNetwork.Parse("192.88.99.0/24"),
        IPNetwork.Parse("192.168.0.0/16"), IPNetwork.Parse("198.18.0.0/15"), IPNetwork.Parse("198.51.100.0/24"),
        IPNetwork.Parse("203.0.113.0/24"), IPNetwork.Parse("224.0.0.0/4"), IPNetwork.Parse("240.0.0.0/4") ];
    private static readonly IPNetwork V6Global = IPNetwork.Parse("2000::/3");
    private static readonly IPNetwork[] V6Excluded = [ IPNetwork.Parse("2001::/23"), IPNetwork.Parse("2001:db8::/32"),
        IPNetwork.Parse("2002::/16"), IPNetwork.Parse("3fff::/20") ];

    public static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) return IsPublic(address.MapToIPv4());
        if (address.AddressFamily == AddressFamily.InterNetwork) return !V4Excluded.Any(n => n.Contains(address));
        return address.AddressFamily == AddressFamily.InterNetworkV6 && address.ScopeId == 0
            && V6Global.Contains(address) && !V6Excluded.Any(n => n.Contains(address));
    }
}

public interface IRssDnsResolver
{
    Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken);
}
public sealed class RssDnsResolver : IRssDnsResolver
{
    public Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken) =>
        Dns.GetHostAddressesAsync(host, cancellationToken);
}

public interface IRssSocketConnector
{
    ValueTask<Stream> ConnectAsync(IPAddress address, int port, CancellationToken cancellationToken);
}
public sealed class RssSocketConnector : IRssSocketConnector
{
    public async ValueTask<Stream> ConnectAsync(IPAddress address, int port, CancellationToken cancellationToken)
    {
        var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(new IPEndPoint(address, port), cancellationToken);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch { socket.Dispose(); throw; }
    }
}

public sealed class RssConnectionFactory(IRssDnsResolver resolver, IRssSocketConnector connector)
{
    public async ValueTask<Stream> ConnectAsync(DnsEndPoint endpoint, CancellationToken cancellationToken)
    {
        // Called at the actual TCP connection, never followed by a second implicit DNS lookup.
        RssNetworkPolicy.ValidateUri("https://" + endpoint.Host + ":" + endpoint.Port);
        IPAddress[] addresses;
        try { addresses = await resolver.ResolveAsync(endpoint.Host, cancellationToken); }
        catch (SocketException) { throw new SourceCollectionException(new("DnsResolutionFailed", 502)); }
        if (addresses.Length == 0) throw new SourceCollectionException(new("DnsResolutionFailed", 502));
        if (addresses.Any(a => !RssNetworkPolicy.IsPublic(a)))
            throw new SourceCollectionException(new("UnsafeResolvedAddress", 502));
        foreach (var address in addresses.Distinct().OrderBy(a => a.ToString(), StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { return await connector.ConnectAsync(address, endpoint.Port, cancellationToken); }
            catch (SocketException) { /* Try the next validated address, no HTTP retry. */ }
        }
        throw new SourceCollectionException(new("UpstreamTransportError", 502));
    }
}
