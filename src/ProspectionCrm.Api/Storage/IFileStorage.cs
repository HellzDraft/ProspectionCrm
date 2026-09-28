namespace ProspectionCrm.Api.Storage;

public interface IFileStorage
{
    // The caller owns content; an existing key is never overwritten.
    Task SaveAsync(string storageKey, Stream content, CancellationToken cancellationToken = default);

    // The caller must dispose the returned stream.
    Task<Stream> OpenReadAsync(string storageKey, CancellationToken cancellationToken = default);
    Task<bool> ExistsAsync(string storageKey, CancellationToken cancellationToken = default);
    Task DeleteAsync(string storageKey, CancellationToken cancellationToken = default);
}
