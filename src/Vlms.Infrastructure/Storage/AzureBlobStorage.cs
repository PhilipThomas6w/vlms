using Azure.Storage.Blobs;
using Vlms.Domain;

namespace Vlms.Infrastructure.Storage;

/// <summary>
/// Real <see cref="IBlobStorage"/> implementation backed by Azure Blob Storage
/// (adr/0001-technology-stack.md). Not yet wired into Vlms.Web's DI container — no UI consumes
/// <c>CertificateService</c> yet (this increment is service-layer only, STATE.md), so there is
/// nothing to point it at a real storage account for. Kept structurally correct and buildable in
/// the meantime, the same way <c>EntraCurrentUserContext</c>/Microsoft.Identity.Web were built out
/// against placeholder Entra config before a live tenant existed (Program.cs, appsettings.json
/// "AzureAd" section) — wire a "BlobStorage" config section + DI registration when the first
/// consumer (a Teacher-facing "mark complete" page, or similar) lands.
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
