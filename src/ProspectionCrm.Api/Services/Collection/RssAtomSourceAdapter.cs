using Microsoft.Extensions.Options;

namespace ProspectionCrm.Api.Services.Collection;

public sealed class RssAtomSourceAdapter(IRssFeedTransport transport, RssAtomFeedParser parser,
    IOptions<RssCollectionOptions> options) : ISourceAdapter
{
    public string SourceTypeCode => "rss";
    public SourceAdapterError? Validate(SourceAdapterContext context)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(context.SearchUrl)) return new("MissingFeedUrl", 409);
            RssCriteria.Parse(context.CriteriaJson, options.Value.DefaultMaxItems);
            RssNetworkPolicy.ValidateUri(context.SearchUrl);
            return null;
        }
        catch (SourceCollectionException exception) { return exception.Error; }
    }
    public async Task<SourceAdapterResult> CollectAsync(SourceAdapterContext context, CancellationToken cancellationToken)
    {
        var criteria = RssCriteria.Parse(context.CriteriaJson, options.Value.DefaultMaxItems);
        var uri = RssNetworkPolicy.ValidateUri(context.SearchUrl);
        var response = await transport.FetchAsync(uri, cancellationToken);
        return parser.Parse(response, criteria, options.Value.MaxResponseBytes, cancellationToken);
    }
}
