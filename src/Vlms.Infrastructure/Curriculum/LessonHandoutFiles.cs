using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace Vlms.Infrastructure.Curriculum;

/// <summary>
/// The rules for a lesson handout's file (docs/adr/0016), shared by the upload in a suggestion and the curriculum seeder:
/// <list type="bullet">
/// <item>Only PDF, Word (.docx), JPEG and PNG, decided by the file's content as well as its extension (<see cref="Matches"/>): a
/// file whose first bytes are not those of its type is refused, so a renamed script or page is never stored as a handout.</item>
/// <item>At most <see cref="MaxBytes"/>.</item>
/// <item>The stored name is made here (<see cref="SafeFileName"/>), never taken as a path from the client.</item>
/// <item>The blob key names the content (<see cref="BlobKey"/>), so a handout a lesson version refers to is never overwritten.</item>
/// </list>
/// Downloads follow <see cref="LessonFileTypes"/>: PDF and pictures inline, Word as an attachment, always <c>nosniff</c>.
/// </summary>
public static class LessonHandoutFiles
{
    public const int MaxBytes = 20 * 1024 * 1024;
    public const int MaxDisplayNameLength = 200;
    private const int MaxStemLength = 100;

    public const string TypesMessage = "A handout must be a PDF, a Word document (.docx), a JPEG or a PNG.";

    /// <summary>The extensions accepted, lower case.</summary>
    public static readonly IReadOnlyList<string> Extensions = [".pdf", ".docx", ".jpg", ".jpeg", ".png"];

    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>Whether <paramref name="content"/> really is a file of the type <paramref name="extension"/> names.</summary>
    public static bool Matches(string extension, byte[] content) => extension.ToLowerInvariant() switch
    {
        ".pdf" => content.AsSpan().StartsWith("%PDF-"u8),
        ".png" => content.AsSpan().StartsWith(PngSignature),
        ".jpg" or ".jpeg" => content.Length >= 3 && content[0] == 0xFF && content[1] == 0xD8 && content[2] == 0xFF,
        ".docx" => IsWordDocument(content),
        _ => false
    };

    // A .docx is a zip package holding [Content_Types].xml and word/document.xml. Only the zip's directory is read.
    private static bool IsWordDocument(byte[] content)
    {
        if (!content.AsSpan().StartsWith("PK\u0003\u0004"u8))
        {
            return false;
        }

        try
        {
            using var zip = new ZipArchive(new MemoryStream(content, writable: false), ZipArchiveMode.Read);
            return zip.GetEntry("[Content_Types].xml") is not null && zip.GetEntry("word/document.xml") is not null;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    /// <summary>
    /// The checked extension of a handout, or throws <see cref="InvalidOperationException"/> with a message people can read
    /// when the name's extension is not allowed or the content is not of that type.
    /// </summary>
    public static string CheckedExtension(string fileName, byte[] content)
    {
        var extension = Path.GetExtension(BareName(fileName)).ToLowerInvariant();
        if (!Extensions.Contains(extension) || !Matches(extension, content))
        {
            throw new InvalidOperationException(TypesMessage);
        }
        return extension;
    }

    /// <summary>The name with any directory part removed.</summary>
    private static string BareName(string fileName)
    {
        var name = fileName.Replace('\\', '/');
        return name[(name.LastIndexOf('/') + 1)..].Trim();
    }

    /// <summary>
    /// The name people see by default: the file's name without directory or extension, with characters that cannot be in a
    /// file name, control characters and repeated spaces removed; "Handout" if nothing is left.
    /// </summary>
    public static string DefaultDisplayName(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(BareName(fileName));
        var builder = new StringBuilder(stem.Length);
        foreach (var c in stem)
        {
            if (char.IsControl(c) || c is '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*')
            {
                builder.Append(' ');
            }
            else
            {
                builder.Append(c);
            }
        }
        var cleaned = string.Join(' ', builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries)).Trim('.', ' ');
        if (cleaned.Length == 0)
        {
            return "Handout";
        }
        return cleaned.Length > MaxStemLength ? cleaned[..MaxStemLength].TrimEnd() : cleaned;
    }

    /// <summary>The stored (download) name: <see cref="DefaultDisplayName"/> plus the checked extension.</summary>
    public static string SafeFileName(string fileName, string extension) => DefaultDisplayName(fileName) + extension;

    /// <summary>A display name as entered in a suggestion: trimmed, required, at most <see cref="MaxDisplayNameLength"/>.</summary>
    public static string CheckedDisplayName(string? displayName)
    {
        var name = string.Join(' ', (displayName ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (name.Length == 0)
        {
            throw new InvalidOperationException("Give each handout a name.");
        }
        if (name.Length > MaxDisplayNameLength)
        {
            throw new InvalidOperationException($"A handout's name must be at most {MaxDisplayNameLength} characters.");
        }
        return name;
    }

    /// <summary>The content-addressed key: <c>lesson-handouts/{scope}/{first 16 hex of SHA-256}{extension}</c>. No caller text in it.</summary>
    public static string BlobKey(string scope, byte[] content, string extension) =>
        $"lesson-handouts/{scope}/{Convert.ToHexStringLower(SHA256.HashData(content))[..16]}{extension}";

    /// <summary>Reads at most <see cref="MaxBytes"/>, refusing anything larger.</summary>
    public static async Task<byte[]> ReadBoundedAsync(Stream content, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await content.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > MaxBytes)
            {
                throw new InvalidOperationException($"A handout must be at most {MaxBytes / (1024 * 1024)} MB.");
            }
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }
}
