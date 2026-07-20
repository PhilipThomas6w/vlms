using Azure.Storage.Blobs;
using Vlms.Domain;

namespace Vlms.Infrastructure.Storage;

/// <summary>
/// Real <see cref="IBlobStorage"/> implementation backed by Azure Blob Storage
/// (adr/0001-technology-stack.md). Not yet wired into Vlms.Web's DI container — no UI consumes
/// <c>CertificateService</c> yet (this increment is service-layer only, STATE.md), so there is
/// nothing to point it at a real storage account for. Kept structurally correct and buildable in
/// the meantime — wire a "BlobStorage" config section + DI registration when the first consumer (a
/// Teacher-facing "mark complete" page, or similar) lands. Superseded by ADR-0005's hosting pivot
/// (self-hosted VPS local disk, not Azure Blob Storage) — see STATE.md Next item 1; this class is
/// retained only until that item lands.
/// </summary>
public sealed class AzureBlobStorage : IBlobStorage
{
    private readonly BlobContainerClient _containerClient;

    public AzureBlobStorage(BlobServiceClient blobServiceClient, string containerName)
    {
        _containerClient = blobServiceClient.GetBlobContainerClient(containerName);
    }

    public async Task UploadAsync(string blobKey, byte[] content, CancellationToken ct = default)
    {
        await _containerClient.CreateIfNotExistsAsync(cancellationToken: ct);
        using var stream = new MemoryStream(content);
        await _containerClient.UploadBlobAsync(blobKey, stream, ct);
    }
}
