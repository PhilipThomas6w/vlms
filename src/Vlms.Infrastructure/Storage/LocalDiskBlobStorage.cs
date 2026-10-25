using Vlms.Domain;

namespace Vlms.Infrastructure.Storage;

/// <summary>
/// Real <see cref="IBlobStorage"/> implementation backed by the local filesystem
/// (adr/0005-hosting-pivot-self-hosted-vps-postgresql.md: certificates/lesson assets live on the
/// self-hosted VPS's own disk, not Azure Blob Storage). Supersedes <c>AzureBlobStorage.cs</c>
/// (removed in the same commit as this class, along with the <c>Azure.Storage.Blobs</c> package
/// reference) — STATE.md's hosting-pivot item 3.
///
/// Not yet wired into Vlms.Web's DI container/appsettings.json: no UI consumes
/// <see cref="Vlms.Infrastructure.Progress.CertificateService"/> yet (confirmed unchanged from the
/// original progress-tracking increment — no page/service anywhere in <c>src/Vlms.Web</c>
/// references <c>CertificateService</c> or <see cref="IBlobStorage"/>), so there is still nothing to
/// point a real root path at from Program.cs. Kept structurally correct and buildable in the
/// meantime, the same "not yet wired" status <c>AzureBlobStorage</c> itself carried before this
/// pivot — wire a <c>"Storage:LocalRootPath"</c> config section + DI registration when the first
/// consumer (a Teacher-facing "mark complete" page, or similar) lands.
///
/// This class only writes to the local <see cref="_rootPath"/> directory; the off-box copy is
/// <c>deploy/backup/files-backup.sh</c> (nightly sync to the S3-compatible bucket, deploy/README.md §9).
/// </summary>
public sealed class LocalDiskBlobStorage : IBlobStorage
{
    /// <summary>
    /// Default root directory when no explicit path is supplied — a sensible local-dev default,
    /// relative to the process's working directory (matches how <c>appsettings.json</c>'s
    /// PostgreSQL/CommunicationServices sections are all "local-dev defaults only", per their own
    /// comments). A real deployment overrides this via the <c>"Storage:LocalRootPath"</c> config key
    /// once something is wired to read it.
    /// </summary>
    public const string DefaultRootPath = "App_Data/blobs";

    private readonly string _rootPath;

    public LocalDiskBlobStorage(string? rootPath = null)
    {
        _rootPath = Path.GetFullPath(string.IsNullOrWhiteSpace(rootPath) ? DefaultRootPath : rootPath);
    }

    public async Task UploadAsync(string blobKey, byte[] content, CancellationToken ct = default)
    {
        var path = ResolvePath(blobKey);

        // Directory creation if missing — CertificateService's blob keys are scoped per-student
        // (certificates/{studentId}/{completionId}.pdf), so most uploads need a new subdirectory the
        // first time a given student gets a certificate. Real Azure Blob Storage has no directory
        // concept to create; a real filesystem does.
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException($"Could not determine a directory for blob key '{blobKey}'.");
        Directory.CreateDirectory(directory);

        await File.WriteAllBytesAsync(path, content, ct);
    }

    public async Task<byte[]?> ReadAsync(string blobKey, CancellationToken ct = default)
    {
        // Same ResolvePath validation as UploadAsync, so a key can never read outside the root.
        var path = ResolvePath(blobKey);
        return File.Exists(path) ? await File.ReadAllBytesAsync(path, ct) : null;
    }

    public Task<bool> ExistsAsync(string blobKey, CancellationToken ct = default) =>
        Task.FromResult(File.Exists(ResolvePath(blobKey)));

    /// <summary>
    /// Maps a blob key onto a real filesystem path under <see cref="_rootPath"/>, rejecting any key
    /// that could otherwise escape it. <see cref="IBlobStorage.UploadAsync"/>'s contract accepts any
    /// string — today's only real caller (<c>CertificateService</c>) always passes a well-formed,
    /// internally-generated key (<c>certificates/{studentId}/{completionId}.pdf</c>), but the
    /// interface itself makes no such guarantee, and this codebase treats injection-class concerns as
    /// worth defending in depth even against a currently-safe caller (the same reasoning behind
    /// every service-layer role check that duplicates a page-level policy gate).
    /// </summary>
    private string ResolvePath(string blobKey)
    {
        if (string.IsNullOrWhiteSpace(blobKey))
        {
            throw new ArgumentException("Blob key must not be empty.", nameof(blobKey));
        }

        // A NUL or other control character has no place in a key (and NUL truncates paths in some APIs).
        if (blobKey.Any(char.IsControl))
        {
            throw new ArgumentException("Blob key must not contain control characters.", nameof(blobKey));
        }

        // Reject absolute paths (Windows drive-qualified, UNC, or POSIX-rooted) outright — an
        // absolute key would otherwise ignore _rootPath entirely once combined.
        if (Path.IsPathRooted(blobKey))
        {
            throw new ArgumentException($"Blob key '{blobKey}' must be a relative path.", nameof(blobKey));
        }

        // Normalize to forward slashes (the convention every real blob key in this codebase already
        // uses — see CertificateService.GenerateAsync) and reject "." / ".." segments explicitly,
        // rather than relying solely on the containment check below — a malformed or malicious key
        // should fail with a clear reason, not just happen to be caught downstream.
        var segments = blobKey.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            throw new ArgumentException($"Blob key '{blobKey}' has no path segments.", nameof(blobKey));
        }

        foreach (var segment in segments)
        {
            if (segment is "." or ".." || segment.Contains(':'))
            {
                throw new ArgumentException(
                    $"Blob key '{blobKey}' contains an invalid path segment ('{segment}').", nameof(blobKey));
            }
        }

        var combined = Path.GetFullPath(Path.Combine(_rootPath, Path.Combine(segments)));

        // Defense in depth: even after rejecting rooted paths and "."/".." segments above, confirm
        // the fully-resolved path still lands inside _rootPath before ever touching the filesystem.
        var rootWithSeparator = _rootPath.EndsWith(Path.DirectorySeparatorChar)
            ? _rootPath
            : _rootPath + Path.DirectorySeparatorChar;
        if (!combined.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"Blob key '{blobKey}' resolves outside the storage root.", nameof(blobKey));
        }

        return combined;
    }
}
