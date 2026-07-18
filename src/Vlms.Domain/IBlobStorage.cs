namespace Vlms.Domain;

/// <summary>
/// Storage abstraction for generated documents — currently just certificate PDFs
/// (<see cref="Certificate"/>, adr/0001-technology-stack.md: Azure Blob Storage). Vlms.Infrastructure
/// provides the real Azure Blob Storage-backed implementation (<c>Storage/AzureBlobStorage.cs</c>);
/// tests use an in-memory fake — see openwiki/testing.md. Lives in Vlms.Domain, alongside
/// <see cref="ICurrentUserContext"/>, for the same reason: a technology-agnostic abstraction that
/// both layers can reference without Vlms.Domain taking on an Azure SDK dependency.
/// </summary>
public interface IBlobStorage
{
    /// <summary>Uploads (creating or overwriting) the blob at <paramref name="blobKey"/>.</summary>
    Task UploadAsync(string blobKey, byte[] content, CancellationToken ct = default);
}
