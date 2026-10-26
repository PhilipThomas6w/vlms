using Markdig;
using Markdig.Renderers;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Vlms.Infrastructure.Curriculum;

/// <summary>
/// Lesson text (Markdown) to HTML (docs/adr/0012-app-hosted-lessons.md decision 1), with Markdig.
///
/// <b>Safety properties every change must keep</b> (tests: LessonMarkdownTests):
/// <list type="bullet">
/// <item>Raw HTML is disabled (<c>DisableHtml()</c>): a <c>&lt;script&gt;</c> or any other tag in the text is shown
/// as text, never markup.</item>
/// <item>Links keep only <c>http:</c>, <c>https:</c>, <c>mailto:</c>, <c>tel:</c>, a fragment or a relative path; any other
/// scheme (<c>javascript:</c>, <c>data:</c>, <c>vbscript:</c>...) loses its link and keeps its text. Every link gets
/// <c>rel="noopener"</c>.</item>
/// <item>Pictures come only from the lesson's own stored pictures: an image's address is a bare file name, resolved
/// by the caller to an app URL (<c>/lessons/pictures/{id}</c>); anything that does not resolve is shown as its text
/// alternative, never fetched. Every picture gets <c>loading="lazy"</c>.</item>
/// </list>
/// </summary>
public static class LessonMarkdown
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables()
        .UseEmphasisExtras()
        .DisableHtml()
        .Build();

    private static readonly string[] SafeSchemes = ["http:", "https:", "mailto:", "tel:"];

    /// <summary>The address of every picture in the text, in order of appearance (duplicates included).</summary>
    public static IReadOnlyList<string> PictureReferences(string markdown) =>
        Markdown.Parse(markdown, Pipeline).Descendants<LinkInline>()
            .Where(l => l.IsImage)
            .Select(l => l.Url ?? string.Empty)
            .ToList();

    /// <summary>Every picture in the text as its address and text alternative, in order of appearance.</summary>
    public static IReadOnlyList<(string Url, string Alt)> Pictures(string markdown) =>
        Markdown.Parse(markdown, Pipeline).Descendants<LinkInline>()
            .Where(l => l.IsImage)
            .Select(l => (l.Url ?? string.Empty, AltText(l).Trim()))
            .ToList();

    /// <summary>
    /// Whether <paramref name="reference"/> is a bare file name (no folder, scheme, query or fragment), the only kind of
    /// picture address lesson text may use.
    /// </summary>
    public static bool IsBareFileName(string reference) =>
        !string.IsNullOrWhiteSpace(reference)
        && reference.IndexOfAny(['/', '\\', ':', '?', '#']) < 0
        && reference is not "." and not "..";

    /// <summary>
    /// Renders <paramref name="markdown"/> to HTML. <paramref name="pictureUrl"/> maps a picture's file name to its app
    /// URL, or null when there is no such picture.
    /// </summary>
    public static string ToHtml(string markdown, Func<string, string?> pictureUrl)
    {
        var document = Markdown.Parse(markdown, Pipeline);

        foreach (var link in document.Descendants<LinkInline>().ToList())
        {
            if (link.IsImage)
            {
                var url = link.Url is not null && IsBareFileName(link.Url) ? pictureUrl(link.Url) : null;
                if (url is null)
                {
                    link.ReplaceBy(new LiteralInline(AltText(link)), copyChildren: false);
                    continue;
                }
                link.Url = url;
                link.Title = null;
                var attributes = link.GetAttributes();
                attributes.AddPropertyIfNotExist("loading", "lazy");
                attributes.AddPropertyIfNotExist("decoding", "async");
            }
            else if (!IsSafeLink(link.Url))
            {
                // Keep the words, drop the link.
                link.ReplaceBy(new ContainerInline(), copyChildren: true);
            }
            else
            {
                link.GetAttributes().AddPropertyIfNotExist("rel", "noopener");
            }
        }

        foreach (var autolink in document.Descendants<AutolinkInline>().ToList())
        {
            if (!autolink.IsEmail && !IsSafeLink(autolink.Url))
            {
                autolink.ReplaceBy(new LiteralInline(autolink.Url), copyChildren: false);
            }
            else
            {
                autolink.GetAttributes().AddPropertyIfNotExist("rel", "noopener");
            }
        }

        using var writer = new StringWriter();
        var renderer = new HtmlRenderer(writer);
        Pipeline.Setup(renderer);
        renderer.Render(document);
        writer.Flush();
        return writer.ToString();
    }

    private static string AltText(LinkInline image) =>
        string.Concat(image.Descendants<LiteralInline>().Select(l => l.Content.ToString()));

    private static bool IsSafeLink(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        // Browsers ignore control characters and whitespace inside a scheme ("java\tscript:"), so judge the URL without them.
        var compact = new string(url.Where(c => !char.IsControl(c) && !char.IsWhiteSpace(c)).ToArray());
        var colon = compact.IndexOf(':');
        if (colon < 0)
        {
            return true;
        }

        // A colon after the first '/', '?' or '#' is part of a relative path, not a scheme.
        var firstDelimiter = compact.IndexOfAny(['/', '?', '#']);
        if (firstDelimiter >= 0 && firstDelimiter < colon)
        {
            return true;
        }

        return SafeSchemes.Any(s => compact.StartsWith(s, StringComparison.OrdinalIgnoreCase));
    }
}
