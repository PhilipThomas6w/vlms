using Microsoft.Extensions.Options;
using Vlms.Domain;

namespace Vlms.Infrastructure.Curriculum;

/// <summary>
/// Accepts a lesson file upload for a proposal (FR-007). Teachers only. Validates size, extension
/// (allow-list), the browser-declared content type and the file's leading bytes, then stores it
/// under the proposer's pending key prefix (<see cref="PendingKeys"/>). Nothing uploaded here is
/// reachable through the lesson library: only <see cref="LessonFile"/> rows are served there, and
/// a row is created only when an Approver approves the proposal that carries the key.
/// </summary>
public sealed class LessonUploadService
{
    public const int MaxFileNameLength = 150;

    private static readonly Dictionary<string, string[]> AllowedContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".pdf"] = ["application/pdf"],
        [".docx"] = ["application/vnd.openxmlformats-officedocument.wordprocessingml.document"],
        [".doc"] = ["application/msword"],
        [".pptx"] = ["application/vnd.openxmlformats-officedocument.presentationml.presentation"],
        [".xlsx"] = ["application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"],
        [".png"] = ["image/png"],
        [".jpg"] = ["image/jpeg"],
        [".jpeg"] = ["image/jpeg"],
        [".html"] = ["text/html"],
        [".txt"] = ["text/plain"]
    };

    private static readonly byte[] Pdf = [0x25, 0x50, 0x44, 0x46, 0x2D];
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF];
    private static readonly byte[] Zip = [0x50, 0x4B, 0x03, 0x04];
    private static readonly byte[] Ole = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];

    private readonly IBlobStorage _blobs;
    private readonly ICurrentUserContext _currentUser;
    private readonly long _maxFileBytes;

    public LessonUploadService(IBlobStorage blobs, ICurrentUserContext currentUser, IOptions<LessonUploadOptions> options)
    {
        _blobs = blobs;
        _currentUser = currentUser;
        _maxFileBytes = options.Value.MaxFileBytes;
    }

    public static IReadOnlyCollection<string> AllowedExtensions => AllowedContentTypes.Keys;

    public long MaxFileBytes => _maxFileBytes;

    /// <summary>
    /// Reads at most <see cref="MaxFileBytes"/> from <paramref name="content"/>, validates it and
    /// stores it as a pending file. Throws <see cref="InvalidOperationException"/> with a
    /// teacher-readable message on any validation failure.
    /// </summary>
    public async Task<ProposedLessonFile> UploadAsync(
        string fileName, string? declaredContentType, Stream content, CancellationToken ct = default)
    {
        if (!_currentUser.HasRole(Role.Teacher))
        {
            throw new UnauthorizedAccessException("Caller does not hold the Teacher role.");
        }

        var userId = _currentUser.UserId ?? throw new UnauthorizedAccessException("Caller has no resolved UserId.");

        var displayName = CleanFileName(fileName);
        var extension = Path.GetExtension(displayName);
        if (!AllowedContentTypes.TryGetValue(extension, out var allowedTypes))
        {
            throw new InvalidOperationException(
                $"'{displayName}' is not an accepted file type. Accepted: {string.Join(", ", AllowedContentTypes.Keys.Order())}.");
        }

        var declared = declaredContentType?.Split(';')[0].Trim();
        if (!string.IsNullOrEmpty(declared)
            && !string.Equals(declared, "application/octet-stream", StringComparison.OrdinalIgnoreCase)
            && !allowedTypes.Contains(declared, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"'{displayName}' has a content type ('{declared}') that does not match its extension.");
        }

        var bytes = await ReadBoundedAsync(content, displayName, ct);
        if (bytes.Length == 0)
        {
            throw new InvalidOperationException($"'{displayName}' is empty.");
        }

        if (!SignatureMatches(extension, bytes))
        {
            throw new InvalidOperationException($"'{displayName}' does not look like a genuine {extension} file.");
        }

        var key = PendingKeys.New(userId, displayName);
        await _blobs.UploadAsync(key, bytes, ct);
        return new ProposedLessonFile { FileName = displayName, BlobKey = key };
    }

    private async Task<byte[]> ReadBoundedAsync(Stream content, string displayName, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await content.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > _maxFileBytes)
            {
                throw new InvalidOperationException(
                    $"'{displayName}' is larger than the {_maxFileBytes / (1024 * 1024)} MB limit.");
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private static string CleanFileName(string fileName)
    {
        // Strip any client-supplied directory part; the name is display text, never a path.
        var name = fileName.Replace('\\', '/');
        name = name[(name.LastIndexOf('/') + 1)..].Trim();
        if (name.Length == 0 || name.Any(char.IsControl))
        {
            throw new InvalidOperationException("The file has no usable name.");
        }

        if (name.Length > MaxFileNameLength)
        {
            throw new InvalidOperationException($"The file name is longer than {MaxFileNameLength} characters.");
        }

        return name;
    }

    private static bool SignatureMatches(string extension, byte[] bytes) => extension.ToLowerInvariant() switch
    {
        ".pdf" => StartsWith(bytes, Pdf),
        ".png" => StartsWith(bytes, Png),
        ".jpg" or ".jpeg" => StartsWith(bytes, Jpeg),
        ".docx" or ".pptx" or ".xlsx" => StartsWith(bytes, Zip),
        ".doc" => StartsWith(bytes, Ole),
        // Text formats have no signature; reject binary content (NUL bytes) instead.
        ".html" or ".txt" => !bytes.Take(8192).Contains((byte)0),
        _ => false
    };

    private static bool StartsWith(byte[] bytes, byte[] prefix) =>
        bytes.Length >= prefix.Length && bytes.AsSpan(0, prefix.Length).SequenceEqual(prefix);
}

/// <summary>Pending blob keys: <c>lessons/pending/{userId}/{guid}/{safe name}</c>.</summary>
public static class PendingKeys
{
    public const string Prefix = "lessons/pending/";

    public static string New(int userId, string displayName)
    {
        var safe = new string(displayName.Select(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_').ToArray());
        return $"{Prefix}{userId}/{Guid.NewGuid():N}/{safe}";
    }

    public static bool BelongsTo(string blobKey, int userId) =>
        blobKey.StartsWith($"{Prefix}{userId}/", StringComparison.Ordinal) && !blobKey.Contains("..");
}
