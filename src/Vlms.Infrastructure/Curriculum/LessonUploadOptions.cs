namespace Vlms.Infrastructure.Curriculum;

/// <summary>Limits for lesson file uploads (FR-007). Bound from the "LessonUploads" configuration section.</summary>
public sealed class LessonUploadOptions
{
    public const string SectionName = "LessonUploads";
    public const long DefaultMaxFileBytes = 25L * 1024 * 1024;

    /// <summary>Largest accepted file, in bytes (default 25 MB).</summary>
    public long MaxFileBytes { get; set; } = DefaultMaxFileBytes;
}
