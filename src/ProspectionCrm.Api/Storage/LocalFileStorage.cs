using Microsoft.Extensions.Options;

namespace ProspectionCrm.Api.Storage;

public sealed class LocalFileStorage : IFileStorage
{
    private readonly string rootPath;
    private readonly string rootPrefix;
    private readonly StringComparison pathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public LocalFileStorage(IOptions<FileStorageOptions> options, IHostEnvironment environment)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Value.RootPath);
        rootPath = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(options.Value.RootPath, environment.ContentRootPath));
        rootPrefix = Path.EndsInDirectorySeparator(rootPath)
            ? rootPath : rootPath + Path.DirectorySeparatorChar;
    }

    public async Task SaveAsync(string storageKey, Stream content, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(content);
        var path = ResolvePath(storageKey);
        if (File.Exists(path) || Directory.Exists(path))
            throw new IOException("A file or directory already exists for this storage key.");

        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".upload-{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 81920, FileOptions.Asynchronous))
            {
                await content.CopyToAsync(output, cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            // Publish only complete content; Move also rejects concurrent collisions.
            File.Move(temporaryPath, path, overwrite: false);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    public Task<Stream> OpenReadAsync(string storageKey, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Stream stream = new FileStream(ResolvePath(storageKey), FileMode.Open, FileAccess.Read,
            FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Task.FromResult(stream);
    }

    public Task<bool> ExistsAsync(string storageKey, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(File.Exists(ResolvePath(storageKey)));
    }

    public Task DeleteAsync(string storageKey, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = ResolvePath(storageKey);
        try
        {
            File.Delete(path);
        }
        catch (DirectoryNotFoundException)
        {
            // A missing parent also means that there is no file to delete.
        }
        return Task.CompletedTask;
    }

    private string ResolvePath(string storageKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageKey);
        // Treat both separators consistently, including on Unix. Forbid drive names and NTFS streams.
        var key = storageKey.Replace('\\', '/');
        if (Path.IsPathRooted(key) || key.Contains(':') || key.EndsWith('/'))
            throw new ArgumentException("Storage keys must be relative file paths.", nameof(storageKey));

        foreach (var segment in key.Split('/'))
        {
            if (segment is "." or "..")
                continue; // GetFullPath resolves these before the containment check.
            if (segment.Length == 0 || segment.EndsWith('.') || segment.EndsWith(' ')
                || segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new ArgumentException("Invalid storage key segment.", nameof(storageKey));
        }

        var path = Path.GetFullPath(key, rootPath);
        if (!path.StartsWith(rootPrefix, pathComparison) || string.Equals(path, rootPath, pathComparison))
            throw new ArgumentException("Storage key resolves outside the storage root.", nameof(storageKey));

        // Reject existing symbolic links/junctions. The storage directory must remain application-owned;
        // this is not a sandbox against another process concurrently replacing filesystem entries.
        for (var current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new ArgumentException("Storage paths must not contain symbolic links or junctions.", nameof(storageKey));
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            if (string.Equals(current, rootPath, pathComparison))
                break;
        }
        return path;
    }
}
