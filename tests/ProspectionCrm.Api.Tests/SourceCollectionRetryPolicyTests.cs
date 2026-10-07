using Microsoft.Extensions.Options;
using ProspectionCrm.Api.Services.Collection;
using Xunit;

namespace ProspectionCrm.Api.Tests;

public sealed class SourceCollectionRetryPolicyTests
{
    [Theory]
    [InlineData("UpstreamTimeout", null, true)]
    [InlineData("UpstreamTransportError", null, true)]
    [InlineData("DnsResolutionFailed", null, true)]
    [InlineData("UpstreamHttpError", 408, true)]
    [InlineData("UpstreamHttpError", 429, true)]
    [InlineData("UpstreamHttpError", 500, true)]
    [InlineData("UpstreamHttpError", 502, true)]
    [InlineData("UpstreamHttpError", 503, true)]
    [InlineData("UpstreamHttpError", 504, true)]
    [InlineData("UpstreamHttpError", null, false)]
    [InlineData("UpstreamHttpError", 404, false)]
    [InlineData("UpstreamHttpError", 501, false)]
    [InlineData("CollectionInternalError", 500, false)]
    [InlineData("CollectionWorkerError", 500, false)]
    [InlineData("PersistenceFailure", 500, false)]
    [InlineData("UpstreamTlsFailure", null, false)]
    [InlineData("InactiveResource", null, false)]
    [InlineData("InvalidFeed", null, false)]
    [InlineData("UnsafeResolvedAddress", null, false)]
    [InlineData("CollectionAbandoned", null, true)]
    [InlineData("CollectionRecoveryAmbiguous", null, false)]
    [InlineData("Unknown", 503, false)]
    public void ClassificationIsExplicitAndBounded(string code, int? http, bool retry)
    {
        var policy = new SourceCollectionRetryPolicy(Options.Create(new SourceCollectionWorkerOptions()));
        Assert.Equal(retry, policy.CanRetry(1, code, http)); Assert.False(policy.CanRetry(3, code, http));
    }

    [Fact]
    public void BackoffDoublesAndCapsAndUnknownDetailsAreNeverCodes()
    {
        var policy = new SourceCollectionRetryPolicy(Options.Create(new SourceCollectionWorkerOptions()));
        Assert.Equal(new[] { 60d, 120d, 240d, 480d, 900d, 900d }, Enumerable.Range(1, 6).Select(x => policy.Backoff(x).TotalSeconds));
        Assert.Equal("CollectionWorkerError", SourceCollectionRetryPolicy.Normalize("SQL http://user:secret@example.org"));
        Assert.Equal("CollectionWorkerError", SourceCollectionRetryPolicy.Normalize(null));
    }
}
