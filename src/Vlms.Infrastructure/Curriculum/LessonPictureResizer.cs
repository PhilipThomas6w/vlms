using SkiaSharp;

namespace Vlms.Infrastructure.Curriculum;

/// <summary>
/// Makes the copy of a lesson picture that the lesson page shows and devices save (docs/adr/0012-app-hosted-lessons.md
/// decision 4): at most <see cref="LongSide"/> pixels on the long side. A picture already that small is kept byte for
/// byte; a larger one is scaled down and re-encoded in its own format (JPEG at quality <see cref="JpegQuality"/>, PNG
/// lossless). The camera's orientation flag is applied before scaling, so the copy is the right way up without it.
/// Uses SkiaSharp (MIT). Returns null when the bytes are not a picture SkiaSharp can read.
/// </summary>
public static class LessonPictureResizer
{
    public const int LongSide = 1600;
    public const int JpegQuality = 85;

    /// <summary>The picture formats lesson text may use, by extension.</summary>
    public static readonly IReadOnlySet<string> SupportedExtensions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png" };

    public static byte[]? Resize(byte[] original)
    {
        using var data = SKData.CreateCopy(original);
        using var codec = SKCodec.Create(data);
        if (codec is null)
        {
            return null;
        }

        var format = codec.EncodedFormat;
        if (format is not (SKEncodedImageFormat.Jpeg or SKEncodedImageFormat.Png))
        {
            return null;
        }

        var origin = codec.EncodedOrigin;
        var width = codec.Info.Width;
        var height = codec.Info.Height;
        if (Math.Max(width, height) <= LongSide && origin == SKEncodedOrigin.TopLeft)
        {
            return original;
        }

        using var decoded = SKBitmap.Decode(codec);
        if (decoded is null)
        {
            return null;
        }

        using var upright = Orient(decoded, origin);
        var scale = Math.Min(1.0, (double)LongSide / Math.Max(upright.Width, upright.Height));
        var info = new SKImageInfo(
            Math.Max(1, (int)Math.Round(upright.Width * scale)),
            Math.Max(1, (int)Math.Round(upright.Height * scale)),
            upright.ColorType, upright.AlphaType, upright.ColorSpace);

        using var resized = upright.Resize(info, new SKSamplingOptions(SKCubicResampler.Mitchell));
        if (resized is null)
        {
            return null;
        }

        using var image = SKImage.FromBitmap(resized);
        using var encoded = image.Encode(format, format == SKEncodedImageFormat.Jpeg ? JpegQuality : 100);
        return encoded?.ToArray();
    }

    // Draws the bitmap the right way up for the three rotations cameras record; the rare mirrored origins are left as
    // they are. A new bitmap is always returned (the caller disposes it).
    private static SKBitmap Orient(SKBitmap source, SKEncodedOrigin origin)
    {
        var quarterTurn = origin is SKEncodedOrigin.RightTop or SKEncodedOrigin.LeftBottom;
        var result = new SKBitmap(quarterTurn ? source.Height : source.Width, quarterTurn ? source.Width : source.Height,
            source.ColorType, source.AlphaType);
        using var canvas = new SKCanvas(result);
        switch (origin)
        {
            case SKEncodedOrigin.RightTop: // 90 degrees clockwise
                canvas.Translate(result.Width, 0);
                canvas.RotateDegrees(90);
                break;
            case SKEncodedOrigin.BottomRight: // 180 degrees
                canvas.RotateDegrees(180, result.Width / 2f, result.Height / 2f);
                break;
            case SKEncodedOrigin.LeftBottom: // 90 degrees anticlockwise
                canvas.Translate(0, result.Height);
                canvas.RotateDegrees(270);
                break;
        }
        using var sourceImage = SKImage.FromBitmap(source);
        canvas.DrawImage(sourceImage, 0, 0, SKSamplingOptions.Default);
        return result;
    }
}
