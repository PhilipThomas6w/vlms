using Vlms.Domain;

namespace Vlms.Tests.Infrastructure;

/// <summary>
/// In-memory <see cref="IBlobStorage"/> test double — no real Azure Storage account needed, same
/// spirit as <see cref="FakeCurrentUserContext"/>. Records every uploaded blob's bytes so tests can
/// assert on what was actually written, not just that upload was called.
/// </summary>
public sealed class InMemoryBlobStorage : IBlobStorage
{
    private readonly Dictionary<string, byte[]> _blobs = new();

    public Task UploadAsync(string blobKey, byte[] content, CancellationToken ct = default)
    {
        _blobs[blobKey] = content;
        return Task.CompletedTask;
    }

    public bool TryGetBlob(string blobKey, out byte[] content) => _blobs.TryGetValue(blobKey, out content!);
}
