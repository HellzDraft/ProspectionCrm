using Microsoft.Extensions.Options;

namespace ProspectionCrm.Api.Services.Collection;

public sealed class SourceCollectionRetryPolicy(IOptions<SourceCollectionWorkerOptions> options)
{
    private static readonly HashSet<string> KnownCodes = new(StringComparer.Ordinal)
    {
        "UpstreamTimeout", "UpstreamTransportError", "DnsResolutionFailed", "UpstreamHttpError", "UpstreamTlsFailure",
        "CollectionWorkerStopping", "CollectionLeaseExpired", "CollectionCancelled", "RequestCancelled", "CollectionAbandoned",
        "CollectionRecoveryAmbiguous", "CollectionAttemptsExhausted", "CollectionWorkerError", "CollectionInternalError", "InvalidRequest",
        "UnsupportedSourceType", "MissingFeedUrl", "InvalidRssCriteria", "UnsafeFeedUrl", "UnsafeRedirect",
        "UnsafeResolvedAddress", "TooManyRedirects", "ResponseTooLarge", "UnsupportedContentType", "InvalidFeed", "NoUsableFeedEntries"
    };
    public static string Normalize(string? code) => code is not null &&
        (KnownCodes.Contains(code) || Enum.GetNames<IngestionErrorCode>().Contains(code, StringComparer.Ordinal))
        ? code : "CollectionWorkerError";

    public bool CanRetry(int attempt, string code, int? upstreamStatus) => attempt < options.Value.MaxAttempts &&
        (code is "UpstreamTimeout" or "UpstreamTransportError" or "DnsResolutionFailed"
            or "CollectionWorkerStopping" or "CollectionLeaseExpired" or "CollectionCancelled" or "RequestCancelled" or "CollectionAbandoned"
        || code == "UpstreamHttpError" && upstreamStatus is 408 or 429 or 500 or 502 or 503 or 504);

    public TimeSpan Backoff(int attempt) => TimeSpan.FromSeconds(Math.Min(options.Value.MaxRetryDelaySeconds,
        options.Value.InitialRetryDelaySeconds * Math.Pow(2, Math.Clamp(attempt - 1, 0, 10))));
}
