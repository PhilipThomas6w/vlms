using SkiaSharp;
using Vlms.Infrastructure.Curriculum;
using Xunit;

namespace Vlms.Tests.Infrastructure;

/// <summary>
/// The safety properties of <see cref="LessonMarkdown"/> (docs/adr/0012-app-hosted-lessons.md decision 1): lesson text
/// cannot carry markup or script, unsafe links lose their link, pictures come only from the lesson's own stored pictures.
/// </summary>
public sealed class LessonMarkdownTests
{
    private static string Render(string markdown) =>
        LessonMarkdown.ToHtml(markdown, name => name == "known.jpg" ? "/lessons/pictures/7" : null);

    [Fact]
    public void ScriptElement_IsRenderedAsText()
    {
        var html = Render("Before\n\n<script>alert('x')</script>\n\nAfter <script>alert(1)</script> inline");

        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("&lt;script&gt;alert('x')&lt;/script&gt;", html);
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", html);
    }

    [Theory]
    [InlineData("<img src=x onerror=alert(1)>")]
    [InlineData("Text with <b onclick=\"alert(1)\">bold</b> inside")]
    [InlineData("<iframe src=\"https://example.com\"></iframe>")]
    [InlineData("<div style=\"position:fixed\">cover</div>")]
    public void InlineHtml_IsRenderedAsText(string markdown)
    {
        var html = Render(markdown);

        Assert.DoesNotContain("<img", html);
        Assert.DoesNotContain("<b ", html);
        Assert.DoesNotContain("<iframe", html);
        Assert.DoesNotContain("<div", html);
        Assert.Contains("&lt;", html);
    }

    [Theory]
    [InlineData("[click](javascript:alert(1))")]
    [InlineData("[click](JavaScript:alert(1))")]
    [InlineData("[click](vbscript:msgbox)")]
    [InlineData("[click](data:text/html;base64,PHNjcmlwdD4=)")]
    [InlineData("[click][ref]\n\n[ref]: javascript:alert(1)")]
    [InlineData("[click](<javascript:alert(1)>)")]
    public void UnsafeLink_LosesItsLinkButKeepsItsText(string markdown)
    {
        var html = Render(markdown);

        Assert.DoesNotContain("href", html);
        Assert.DoesNotContain("javascript:", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("click", html);
    }

    [Fact]
    public void UnsafeAutolink_IsShownAsPlainText()
    {
        var html = Render("See <javascript:alert(1)> now");

        Assert.DoesNotContain("href", html);
        Assert.DoesNotContain("<a", html);
    }

    [Theory]
    [InlineData("[site](https://example.org/page)", "https://example.org/page")]
    [InlineData("[mail](mailto:office@example.org)", "mailto:office@example.org")]
    [InlineData("[anchor](#questions)", "#questions")]
    [InlineData("[library](/lessons)", "/lessons")]
    public void SafeLink_IsKept_WithRelNoopener(string markdown, string href)
    {
        var html = Render(markdown);

        Assert.Contains($"href=\"{href}\"", html);
        Assert.Contains("rel=\"noopener\"", html);
    }

    [Fact]
    public void KnownPicture_PointsAtItsAppUrl_AndLoadsLazily()
    {
        var html = Render("![The saints](known.jpg)");

        Assert.Contains("src=\"/lessons/pictures/7\"", html);
        Assert.Contains("alt=\"The saints\"", html);
        Assert.Contains("loading=\"lazy\"", html);
    }

    [Theory]
    [InlineData("![Missing picture](unknown.jpg)")]
    [InlineData("![Missing picture](https://tracker.example/pixel.gif)")]
    [InlineData("![Missing picture](../R5-02/known.jpg)")]
    [InlineData("![Missing picture](javascript:alert(1))")]
    public void PictureThatIsNotTheLessons_IsShownAsItsTextAlternative(string markdown)
    {
        var html = Render(markdown);

        Assert.DoesNotContain("<img", html);
        Assert.Contains("Missing picture", html);
    }

    [Fact]
    public void PipeTable_RendersAsATable()
    {
        var html = Render("| Ingredient | Amount |\n|---|---|\n| Flour | 12 oz |");

        Assert.Contains("<table>", html);
        Assert.Contains("<th>Ingredient</th>", html);
        Assert.Contains("<td>12 oz</td>", html);
    }

    [Fact]
    public void PictureReferences_AreInOrderOfAppearance()
    {
        var references = LessonMarkdown.PictureReferences("![a](b.jpg)\n\ntext ![c](a.png) and [link](x.jpg)\n\n![d](b.jpg)");

        Assert.Equal(["b.jpg", "a.png", "b.jpg"], references);
    }

    [Theory]
    [InlineData("all-saints.jpg", true)]
    [InlineData("Prosphora loaves.JPG", true)]
    [InlineData("sub/all-saints.jpg", false)]
    [InlineData("..\\secret.jpg", false)]
    [InlineData("https://example.org/a.jpg", false)]
    [InlineData("a.jpg?x=1", false)]
    [InlineData("..", false)]
    [InlineData("", false)]
    public void IsBareFileName(string reference, bool expected) =>
        Assert.Equal(expected, LessonMarkdown.IsBareFileName(reference));
}

/// <summary><see cref="LessonPictureResizer"/>: large pictures come down to 1,600 px on the long side; small ones are untouched.</summary>
public sealed class LessonPictureResizerTests
{
    internal static byte[] MakePicture(int width, int height, SKEncodedImageFormat format)
    {
        using var bitmap = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.SteelBlue);
        }
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(format, 90);
        return data.ToArray();
    }

    private static (int Width, int Height, SKEncodedImageFormat Format) Measure(byte[] bytes)
    {
        using var codec = SKCodec.Create(new MemoryStream(bytes));
        return (codec.Info.Width, codec.Info.Height, codec.EncodedFormat);
    }

    [Fact]
    public void LargeJpeg_IsScaledToTheLongSide_StayingJpeg()
    {
        var resized = LessonPictureResizer.Resize(MakePicture(2400, 3200, SKEncodedImageFormat.Jpeg));

        Assert.NotNull(resized);
        Assert.Equal((1200, 1600, SKEncodedImageFormat.Jpeg), Measure(resized));
    }

    [Fact]
    public void LargePng_IsScaledToTheLongSide_StayingPng()
    {
        var resized = LessonPictureResizer.Resize(MakePicture(3200, 1000, SKEncodedImageFormat.Png));

        Assert.NotNull(resized);
        Assert.Equal((1600, 500, SKEncodedImageFormat.Png), Measure(resized));
    }

    [Fact]
    public void SmallPicture_IsKeptByteForByte()
    {
        var original = MakePicture(800, 600, SKEncodedImageFormat.Jpeg);

        Assert.Same(original, LessonPictureResizer.Resize(original));
    }

    [Fact]
    public void NotAPicture_IsNull() =>
        Assert.Null(LessonPictureResizer.Resize("not a picture"u8.ToArray()));
}
