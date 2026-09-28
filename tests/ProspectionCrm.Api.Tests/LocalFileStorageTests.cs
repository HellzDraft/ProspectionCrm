using System.Text;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using ProspectionCrm.Api.Storage;
using Xunit;

namespace ProspectionCrm.Api.Tests;

public sealed class LocalFileStorageTests : IDisposable
{
    private readonly string testDirectory = Path.Combine(Path.GetTempPath(), "ProspectionCrm.Tests", Guid.NewGuid().ToString("N"));
    private readonly string root;
    private readonly IFileStorage storage;

    public LocalFileStorageTests()
    {
        root = Path.Combine(testDirectory, "storage");
        storage = new LocalFileStorage(Options.Create(new FileStorageOptions { RootPath = "storage" }),
            new TestEnvironment { ContentRootPath = testDirectory });
    }

    [Fact]
    public async Task SaveAndReadPreserveContentAndStreamOwnership()
    {
        using var content = Content("Test content — UTF-8");
        await storage.SaveAsync("file.txt", content);
        Assert.True(content.CanRead);
        await using var stream = await storage.OpenReadAsync("file.txt");
        using var reader = new StreamReader(stream);
        Assert.Equal("Test content — UTF-8", await reader.ReadToEndAsync());
    }

    [Fact]
    public async Task ExistsChangesAfterSave()
    {
        Assert.False(await storage.ExistsAsync("file.txt"));
        using var content = Content("test");
        await storage.SaveAsync("file.txt", content);
        Assert.True(await storage.ExistsAsync("file.txt"));
    }

    [Fact]
    public async Task DeleteRemovesFileAndCanBeRepeated()
    {
        using var content = Content("test");
        await storage.SaveAsync("file.txt", content);
        await storage.DeleteAsync("file.txt");
        Assert.False(await storage.ExistsAsync("file.txt"));
        await storage.DeleteAsync("file.txt");
    }

    [Fact]
    public async Task DeleteMissingFileWithMissingParentsSucceeds()
    {
        await storage.DeleteAsync("missing/parents/file.txt");
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public async Task SaveRefusesOverwriteAndPreservesOriginal()
    {
        using var original = Content("original");
        using var replacement = Content("replacement");
        await storage.SaveAsync("file.txt", original);
        var exception = await Assert.ThrowsAsync<IOException>(() => storage.SaveAsync("file.txt", replacement));
        Assert.Contains("already exists", exception.Message);
        Assert.Equal("original", await File.ReadAllTextAsync(Path.Combine(root, "file.txt")));
    }

    [Fact]
    public async Task SaveCreatesParentsUnderContentRoot()
    {
        using var content = Content("test");
        await storage.SaveAsync("one/two/file.txt", content);
        Assert.True(File.Exists(Path.Combine(root, "one", "two", "file.txt")));
        Assert.NotEqual(Path.GetFullPath("storage"), root);
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("..\\outside.txt")]
    [InlineData("one/../../outside.txt")]
    [InlineData("one\\..\\..\\outside.txt")]
    [InlineData("../storage-sibling/outside.txt")]
    [InlineData("/absolute.txt")]
    [InlineData("C:\\absolute.txt")]
    [InlineData("C:drive-relative.txt")]
    [InlineData("\\\\server\\share\\file.txt")]
    [InlineData("file.txt:stream")]
    [InlineData(".. /outside.txt")]
    [InlineData(".")]
    [InlineData("")]
    [InlineData(" ")]
    public async Task InvalidKeysAreRejectedByEveryOperation(string key)
    {
        using var content = Content("test");
        await Assert.ThrowsAnyAsync<ArgumentException>(() => storage.SaveAsync(key, content));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => storage.OpenReadAsync(key));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => storage.ExistsAsync(key));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => storage.DeleteAsync(key));
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public async Task NativeAbsolutePathIsRejected()
    {
        var path = Path.Combine(testDirectory, "outside.txt");
        using var content = Content("test");
        await Assert.ThrowsAsync<ArgumentException>(() => storage.SaveAsync(path, content));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task CanonicalInternalPathAndDotsInFileNameAreAllowed()
    {
        using var content = Content("test");
        await storage.SaveAsync("one/../two/report..txt", content);
        Assert.True(await storage.ExistsAsync("two/report..txt"));
        Assert.True(File.Exists(Path.Combine(root, "two", "report..txt")));
        Assert.False(Directory.Exists(Path.Combine(root, "one")));
    }

    [Fact]
    public async Task OpenMissingFileFails()
    {
        Directory.CreateDirectory(root);
        await Assert.ThrowsAsync<FileNotFoundException>(() => storage.OpenReadAsync("absent.txt"));
    }

    [Fact]
    public async Task CancelledOperationsDoNotCreateFiles()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        using var content = Content("test");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => storage.SaveAsync("file.txt", content, cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => storage.OpenReadAsync("file.txt", cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => storage.ExistsAsync("file.txt", cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => storage.DeleteAsync("file.txt", cts.Token));
        Assert.False(Directory.Exists(root));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InterruptedWriteLeavesNoPartialFile(bool cancel)
    {
        using var cts = new CancellationTokenSource();
        using var content = new InterruptedStream(cts, cancel);
        if (cancel)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => storage.SaveAsync("file.txt", content, cts.Token));
        else
            await Assert.ThrowsAsync<IOException>(() => storage.SaveAsync("file.txt", content));
        Assert.False(await storage.ExistsAsync("file.txt"));
        Assert.Empty(Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task ConcurrentSavesCannotOverwriteEachOther()
    {
        using var first = Content("first");
        using var second = Content("second");
        var saves = new[] { storage.SaveAsync("file.txt", first), storage.SaveAsync("file.txt", second) };
        await Assert.ThrowsAnyAsync<IOException>(() => Task.WhenAll(saves));
        Assert.Equal(1, saves.Count(task => task.IsCompletedSuccessfully));
        Assert.Equal(1, saves.Count(task => task.IsFaulted));
        Assert.Contains(await File.ReadAllTextAsync(Path.Combine(root, "file.txt")), new[] { "first", "second" });
        Assert.Single(Directory.EnumerateFiles(root));
    }

    private static MemoryStream Content(string text) => new(Encoding.UTF8.GetBytes(text));

    public void Dispose()
    {
        // Only this test's generated temporary directory can be removed.
        var temporaryParent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "ProspectionCrm.Tests"))
            + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(testDirectory).StartsWith(temporaryParent, StringComparison.Ordinal))
            throw new InvalidOperationException("Invalid test cleanup path.");
        if (Directory.Exists(testDirectory))
            Directory.Delete(testDirectory, recursive: true);
    }

    private sealed class TestEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";
        public string ApplicationName { get; set; } = "ProspectionCrm.Api.Tests";
        public string ContentRootPath { get; set; } = string.Empty;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class InterruptedStream(CancellationTokenSource cts, bool cancel) : MemoryStream
    {
        public override async Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken)
        {
            await destination.WriteAsync(new byte[] { 1, 2, 3 }, cancellationToken);
            if (cancel)
            {
                cts.Cancel();
                cancellationToken.ThrowIfCancellationRequested();
            }
            throw new IOException("Simulated input stream failure.");
        }
    }
}
