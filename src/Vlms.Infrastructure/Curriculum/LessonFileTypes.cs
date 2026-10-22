namespace Vlms.Infrastructure.Curriculum;

/// <summary>
/// Maps a lesson file's extension to its Content-Type and decides whether a browser may display it
/// inline (FR-006): PDF and common raster images open in the browser; everything else, including
/// unknown types and SVG (which can carry script), downloads as an attachment.
/// </summary>
public static class LessonFileTypes
{
    public const string FallbackContentType = "application/octet-stream";

    private static readonly Dictionary<string, string> InlineTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".pdf"] = "application/pdf",
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp"
    };

    private static readonly Dictionary<string, string> AttachmentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        [".doc"] = "application/msword",
        [".pptx"] = "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        [".xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"
    };

    public static string ContentTypeFor(string fileName)
    {
        var extension = Path.GetExtension(fileName);
        if (InlineTypes.TryGetValue(extension, out var inline))
        {
            return inline;
        }

        return AttachmentTypes.TryGetValue(extension, out var attachment) ? attachment : FallbackContentType;
    }

    public static bool IsInline(string fileName) => InlineTypes.ContainsKey(Path.GetExtension(fileName));
}
