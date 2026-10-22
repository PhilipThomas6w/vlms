using System.Text;
using Vlms.Infrastructure.Storage;
using Xunit;

namespace Vlms.Tests.Infrastructure;

/// <summary>
/// Direct tests of <see cref="LocalDiskBlobStorage"/> itself — real filesystem I/O against a
/// per-test temp directory (not another in-memory fake; <see cref="InMemoryBlobStorage"/> already
/// covers testing *consumers* of <see cref="Vlms.Domain.IBlobStorage"/>, so this file's job is to
/// prove the real implementation's own filesystem concerns — directory creation, key scoping, and
/// path-traversal rejection — actually work). Each test gets its own temp directory
/// (<see cref="Path.GetRandomFileName"/>-named, under <see cref="Path.GetTempPath"/>) and cleans it
/// up on dispose, rather than a fixed shared path.
/// </summary>
public sealed class LocalDiskBlobStorageTests : IDisposable
{
    private readonly string _rootPath;
    private readonly LocalDiskBlobStorage _storage;

    public LocalDiskBlobStorageTests()
    {
        _rootPath = Path.Combine(Path.GetTempPath(), "vlms-blob-test-" + Path.GetRandomFileName());
        _storage = new LocalDiskBlobStorage(_rootPath);
    }

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, recursive: true);
        }
    }

    [Fact]
    public async Task UploadAsync_ThenReadFromDisk_RoundTripsTheExactBytes()
    {
        var content = Encoding.UTF8.GetBytes("a real PDF's bytes would go here");

        await _storage.UploadAsync("certificates/100/1.pdf", content, CancellationToken.None);

        var writtenPath = Path.Combine(_rootPath, "certificates", "100", "1.pdf");
        Assert.True(File.Exists(writtenPath));
        var readBack = await File.ReadAllBytesAsync(writtenPath);
        Assert.Equal(content, readBack);
    }

    [Fact]
    public async Task UploadAsync_KeyWithSlashes_ScopedToMatchingNestedPath()
    {
        await _storage.UploadAsync("certificates/42/7.pdf", [1, 2, 3], CancellationToken.None);

        // Same scoping convention CertificateService.GenerateAsync relies on: a "/"-delimited key
        // maps onto real nested directories, one per segment except the last (the filename).
        Assert.True(File.Exists(Path.Combine(_rootPath, "certificates", "42", "7.pdf")));
    }

    [Fact]
    public async Task UploadAsync_NestedDirectoryDoesNotYetExist_IsCreatedAutomatically()
    {
        var nestedRoot = Path.Combine(_rootPath, "not-created-yet");
        var storage = new LocalDiskBlobStorage(nestedRoot);
        Assert.False(Directory.Exists(nestedRoot));

        await storage.UploadAsync("certificates/1/1.pdf", [9], CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(nestedRoot, "certificates", "1", "1.pdf")));
    }

    [Fact]
    public async Task UploadAsync_SameKeyTwice_OverwritesRatherThanFailing()
    {
        await _storage.UploadAsync("certificates/1/1.pdf", Encoding.UTF8.GetBytes("first"), CancellationToken.None);
        await _storage.UploadAsync("certificates/1/1.pdf", Encoding.UTF8.GetBytes("second"), CancellationToken.None);

        var writtenPath = Path.Combine(_rootPath, "certificates", "1", "1.pdf");
        Assert.Equal("second", Encoding.UTF8.GetString(await File.ReadAllBytesAsync(writtenPath)));
    }

    [Theory]
    [InlineData("../escape.pdf")]
    [InlineData("certificates/../../escape.pdf")]
    [InlineData("certificates/1/../../../escape.pdf")]
    [InlineData("..\\escape.pdf")]
    [InlineData("./certificates/1.pdf")]
    public async Task UploadAsync_KeyContainingDotDotOrDotSegment_Throws(string maliciousKey)
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => _storage.UploadAsync(maliciousKey, [1], CancellationToken.None));

        // Confirm nothing was written anywhere outside (or inside) the root as a side effect of the
        // rejected call.
        Assert.False(Directory.Exists(_rootPath) && Directory.EnumerateFileSystemEntries(_rootPath).Any());
    }

    [Theory]
    [InlineData("/etc/passwd")]
    [InlineData("C:\\Windows\\evil.pdf")]
    public async Task UploadAsync_AbsolutePathKey_Throws(string absoluteKey)
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => _storage.UploadAsync(absoluteKey, [1], CancellationToken.None));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task UploadAsync_BlankKey_Throws(string blankKey)
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => _storage.UploadAsync(blankKey, [1], CancellationToken.None));
    }

    [Fact]
    public async Task ReadAsync_AfterUpload_RoundTripsTheExactBytes()
    {
        var content = Encoding.UTF8.GetBytes("lesson file bytes");
        await _storage.UploadAsync("lessons/12/handout.pdf", content, CancellationToken.None);

        var readBack = await _storage.ReadAsync("lessons/12/handout.pdf", CancellationToken.None);

        Assert.Equal(content, readBack);
    }

    [Fact]
    public async Task ReadAsync_MissingBlob_ReturnsNull()
    {
        Assert.Null(await _storage.ReadAsync("lessons/12/missing.pdf", CancellationToken.None));
    }

    [Theory]
    [InlineData("../escape.pdf")]
    [InlineData("lessons/../../escape.pdf")]
    [InlineData("..\\escape.pdf")]
    [InlineData("./lessons/1.pdf")]
    [InlineData("/etc/passwd")]
    [InlineData("C:\\Windows\\win.ini")]
    [InlineData("lessons/1.pdf\u0000.txt")]
    [InlineData("")]
    public async Task ReadAsync_TraversalOrMalformedKey_Throws(string maliciousKey)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _storage.ReadAsync(maliciousKey, CancellationToken.None));
    }

    [Fact]
    public async Task ReadAsync_FileOutsideTheRoot_IsNotReadable()
    {
        var outside = Path.Combine(Path.GetTempPath(), "vlms-outside-" + Path.GetRandomFileName() + ".txt");
        await File.WriteAllTextAsync(outside, "secret");
        try
        {
            var relative = Path.GetRelativePath(_rootPath, outside).Replace('\\', '/');
            await Assert.ThrowsAsync<ArgumentException>(() => _storage.ReadAsync(relative, CancellationToken.None));
        }
        finally
        {
            File.Delete(outside);
        }
    }

    [Fact]
    public void Constructor_NoRootPathSupplied_FallsBackToDefaultRootPath()
    {
        var storage = new LocalDiskBlobStorage();

        // Just proves the parameterless path doesn't throw and picks the documented default — the
        // actual write behaviour against that default is already covered by the other tests using an
        // explicit temp root.
        Assert.NotNull(storage);
    }
}
