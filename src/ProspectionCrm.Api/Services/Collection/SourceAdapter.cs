using ProspectionCrm.Api.Dtos.Ingestions;

namespace ProspectionCrm.Api.Services.Collection;

public interface ISourceAdapter
{
    string SourceTypeCode { get; }
    SourceAdapterError? Validate(SourceAdapterContext context);
    Task<SourceAdapterResult> CollectAsync(SourceAdapterContext context, CancellationToken cancellationToken);
}

public sealed record SourceAdapterContext(string? SearchUrl, string CriteriaJson);
public sealed record SourceAdapterSummary(string SourceTypeCode, string FeedFormat, int EntriesRead,
    int EntriesMapped, int EntriesSkipped, bool WasTruncated);
public sealed record SourceAdapterResult(IReadOnlyList<IngestionItemRequest> Items, SourceAdapterSummary Summary);
public sealed record SourceAdapterError(string Code, int StatusCode, int? UpstreamStatusCode = null)
{
    public string Detail => "Source collection could not complete (" + Code + ").";
}

// Contains only controlled metadata. Never attach an upstream exception, URI or response.
public sealed class SourceCollectionException(SourceAdapterError error) : Exception(error.Code)
{
    public SourceAdapterError Error { get; } = error;
}

public sealed class SourceAdapterRegistry
{
    private readonly Dictionary<string, ISourceAdapter> adapters = new(StringComparer.Ordinal);
    public SourceAdapterRegistry(IEnumerable<ISourceAdapter> registrations)
    {
        foreach (var adapter in registrations)
            if (!adapters.TryAdd(adapter.SourceTypeCode, adapter))
                throw new InvalidOperationException("Duplicate source adapter registration.");
    }
    public ISourceAdapter? Find(string code) => adapters.GetValueOrDefault(code);
}
