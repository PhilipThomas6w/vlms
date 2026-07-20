namespace Vlms.Domain;

/// <summary>
/// Storage abstraction for generated documents — currently just certificate PDFs
/// (<see cref="Certificate"/>). Originally backed by Azure Blob Storage
/// (adr/0001-technology-stack.md); superseded by adr/0005-hosting-pivot-self-hosted-vps-postgresql.md
/// — Vlms.Infrastructure now provides a local-disk-backed implementation
/// (<c>Storage/LocalDiskBlobStorage.cs</c>) writing under the self-hosted VPS's own filesystem;
/// tests use an in-memory fake — see openwiki/testing.md. Lives in Vlms.Domain, alongside
/// <see cref="ICurrentUserContext"/>, for the same reason: a technology-agnostic abstraction that
/// both layers can reference without Vlms.Domain taking on any particular storage SDK dependency.
/// </summary>
public interface IBlobStorage
{
    /// <summary>Uploads (creating or overwriting) the blob at <paramref name="blobKey"/>.</summary>
    Task UploadAsync(string blobKey, byte[] content, CancellationToken ct = default);
}
