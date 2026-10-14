using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ProspectionCrm.Api.Services.Integrations;

namespace ProspectionCrm.Api.Tests;

public sealed class IntegrationTlsServer : IAsyncDisposable
{
    private readonly X509Certificate2 root;
    private readonly X509Certificate2 leaf;
    private WebApplication app = null!;
    public Uri Endpoint { get; private set; } = null!;
    public X509Certificate2 Certificate => leaf;
    public int Calls;
    private IntegrationTlsServer(string hostname, bool expired)
    {
        using var rootKey = RSA.Create(2048);
        var rootRequest = new CertificateRequest("CN=Ephemeral integration test CA", rootKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        rootRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
        root = rootRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-3), DateTimeOffset.UtcNow.AddDays(2));
        using var key = RSA.Create(2048);
        var request = new CertificateRequest($"CN={hostname}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder(); san.AddDnsName(hostname);
        if (hostname == "localhost") san.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        using var signed = request.Create(root, DateTimeOffset.UtcNow.AddDays(-2), DateTimeOffset.UtcNow.AddDays(expired ? -1 : 1), RandomNumberGenerator.GetBytes(16));
        using var withKey = signed.CopyWithPrivateKey(key);
        // Schannel needs a temporary key container on Windows. Without PersistKeySet it is
        // removed on disposal; no certificate is installed in any system trust store.
        leaf = X509CertificateLoader.LoadPkcs12(withKey.Export(X509ContentType.Pfx), null,
            OperatingSystem.IsWindows() ? X509KeyStorageFlags.DefaultKeySet : X509KeyStorageFlags.EphemeralKeySet);
    }
    public static async Task<IntegrationTlsServer> StartAsync(Func<HttpContext, Task> action, string hostname = "localhost", bool expired = false)
    {
        var server = new IntegrationTlsServer(hostname, expired);
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(server.leaf)));
        server.app = builder.Build();
        server.app.Run(async context => { Interlocked.Increment(ref server.Calls); await action(context); });
        await server.app.StartAsync();
        var address = server.app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        var uri = new UriBuilder(address) { Path = "/probe" };
        server.Endpoint = uri.Uri;
        return server;
    }
    public SocketsHttpHandler Handler(bool trustRoot = true)
    {
        var handler = IntegrationTransport.CreateHandler();
        if (trustRoot)
        {
            // Real TLS validation using only this isolated CA, with no callback overriding validation.
            handler.SslOptions.CertificateChainPolicy = new X509ChainPolicy
            {
                TrustMode = X509ChainTrustMode.CustomRootTrust, CustomTrustStore = { root },
                VerificationFlags = X509VerificationFlags.NoFlag, RevocationMode = X509RevocationMode.NoCheck,
                DisableCertificateDownloads = true, ApplicationPolicy = { new Oid("1.3.6.1.5.5.7.3.1") }
            };
        }
        return handler;
    }
    public async ValueTask DisposeAsync()
    { await app.DisposeAsync(); leaf.Dispose(); root.Dispose(); }
}
