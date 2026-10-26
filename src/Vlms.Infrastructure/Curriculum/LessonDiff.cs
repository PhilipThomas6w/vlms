using System.Text;
using System.Text.RegularExpressions;
using DiffPlex;
using DiffPlex.Chunkers;

namespace Vlms.Infrastructure.Curriculum;

/// <summary>
/// Tracked changes between two versions of a lesson (docs/adr/0013 decisions 3 and 4): a word-level comparison, with
/// added words in <c>&lt;ins&gt;</c> and removed words in <c>&lt;del&gt;</c>, and added and removed pictures likewise.
///
/// <b>How.</b> Both versions are first rendered by <see cref="LessonMarkdown"/> (raw HTML disabled), and the two HTML
/// texts compared token by token (tags, words, spaces, punctuation; DiffPlex, Apache-2.0). Comparing the rendered text
/// rather than the Markdown means differences of Markdown spelling alone (a "*" bullet for a "-", an escaped character,
/// table padding) are not shown as changes. The result keeps the new version's tags exactly, so it is always well formed:
/// a removed tag is dropped, and removed words appear as <c>&lt;del&gt;</c> text where they stood. Removed pictures are
/// kept, in <c>&lt;del&gt;</c>. Nothing in the result comes from anywhere but the two rendered texts and the
/// <c>ins</c>/<c>del</c> tags added here, so it is as safe as <see cref="LessonMarkdown.ToHtml"/>'s own output.
///
/// Pictures are compared by their stored copy (blob key), not by address: the caller's <c>pictureKey</c> gives a
/// picture's key for its file name, and <c>pictureUrl</c> its address once compared.
/// </summary>
public static partial class LessonDiff
{
    private const string PicturePrefix = "vlms-picture:";

    /// <summary>One side of a comparison: the Markdown and its pictures (file name to blob key and to address).</summary>
    public sealed record Side(string? Body, IReadOnlyDictionary<string, (string BlobKey, string Url)> Pictures)
    {
        public static Side Empty { get; } = new(null, new Dictionary<string, (string, string)>());
    }

    /// <summary>The new side's HTML, with its pictures at their addresses.</summary>
    public static string Render(Side side) =>
        side.Body is null ? "" : LessonMarkdown.ToHtml(side.Body, name => side.Pictures.TryGetValue(name, out var p) ? p.Url : null);

    /// <summary>The tracked changes from <paramref name="before"/> to <paramref name="after"/>, as HTML.</summary>
    public static string Tracked(Side before, Side after)
    {
        var urls = new Dictionary<string, string>(StringComparer.Ordinal);
        string Placeholder(Side side, string name)
        {
            if (!side.Pictures.TryGetValue(name, out var picture))
            {
                return "";
            }
            var key = PicturePrefix + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(picture.BlobKey)));
            urls[key] = picture.Url;
            return key;
        }

        var oldHtml = before.Body is null ? "" : LessonMarkdown.ToHtml(before.Body, n => NullIfEmpty(Placeholder(before, n)));
        var newHtml = after.Body is null ? "" : LessonMarkdown.ToHtml(after.Body, n => NullIfEmpty(Placeholder(after, n)));

        var merged = Merge(oldHtml, newHtml);
        foreach (var (key, url) in urls)
        {
            merged = merged.Replace(key, System.Net.WebUtility.HtmlEncode(url), StringComparison.Ordinal);
        }
        return merged;
    }

    /// <summary>Word-level tracked changes between two plain-text values (a title, a list of tags), as HTML.</summary>
    public static string TrackedText(string before, string after) =>
        Merge(System.Net.WebUtility.HtmlEncode(before), System.Net.WebUtility.HtmlEncode(after));

    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;

    private static string Merge(string oldHtml, string newHtml)
    {
        var chunker = new CustomFunctionChunker(Tokenise);
        var result = Differ.Instance.CreateDiffs(oldHtml, newHtml, ignoreWhiteSpace: false, ignoreCase: false, chunker);
        var oldTokens = result.PiecesOld.ToArray();
        var newTokens = result.PiecesNew.ToArray();

        var output = new StringBuilder(newHtml.Length + 64);
        var newIndex = 0;
        foreach (var block in result.DiffBlocks)
        {
            // Unchanged run before the block (the same tokens on both sides).
            while (newIndex < block.InsertStartB)
            {
                output.Append(newTokens[newIndex++]);
            }

            // A removed stretch whose tags open and close within it (a whole list item, paragraph or table row) keeps
            // them, so it shows where it stood; any other removed tag is dropped.
            Wrap(output, oldTokens, block.DeleteStartA, block.DeleteCountA, "del",
                keepTags: IsBalanced(oldTokens, block.DeleteStartA, block.DeleteCountA));
            Wrap(output, newTokens, block.InsertStartB, block.InsertCountB, "ins", keepTags: true);
            newIndex = block.InsertStartB + block.InsertCountB;
        }

        while (newIndex < newTokens.Length)
        {
            output.Append(newTokens[newIndex++]);
        }

        return output.ToString();
    }

    // Writes a run of tokens with each stretch of text (and pictures) inside one <ins> or <del>. Tags of the new side are
    // written as they are, between stretches, so the result keeps the new version's structure; tags of the old side are
    // dropped. Whitespace alone is written plain.
    private static void Wrap(StringBuilder output, string[] tokens, int start, int count, string tag, bool keepTags)
    {
        var open = false;
        for (var i = start; i < start + count; i++)
        {
            var token = tokens[i];
            var isPicture = token.StartsWith("<img", StringComparison.OrdinalIgnoreCase);
            if (token.StartsWith('<') && !isPicture)
            {
                if (open)
                {
                    output.Append("</").Append(tag).Append('>');
                    open = false;
                }
                if (keepTags)
                {
                    output.Append(token);
                }
                continue;
            }

            if (!open && string.IsNullOrWhiteSpace(token))
            {
                output.Append(token);
                continue;
            }

            if (!open)
            {
                output.Append('<').Append(tag).Append('>');
                open = true;
            }
            output.Append(token);
        }

        if (open)
        {
            output.Append("</").Append(tag).Append('>');
        }
    }

    private static readonly HashSet<string> VoidElements = new(StringComparer.OrdinalIgnoreCase) { "img", "br", "hr" };

    private static bool IsBalanced(string[] tokens, int start, int count)
    {
        var open = new Stack<string>();
        var anyTag = false;
        for (var i = start; i < start + count; i++)
        {
            var token = tokens[i];
            if (!token.StartsWith('<') || token.StartsWith("<img", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var closing = token.StartsWith("</", StringComparison.Ordinal);
            var name = TagName().Match(token).Groups[1].Value;
            if (VoidElements.Contains(name))
            {
                continue;
            }
            anyTag = true;
            if (!closing)
            {
                open.Push(name);
            }
            else if (open.Count == 0 || !string.Equals(open.Pop(), name, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }
        return anyTag && open.Count == 0;
    }

    [GeneratedRegex(@"^</?([A-Za-z0-9]+)")]
    private static partial Regex TagName();

    private static string[] Tokenise(string html) =>
        TokenPattern().Matches(html).Select(m => m.Value).ToArray();

    // A tag, a character reference, a word (letters, digits, apostrophes and hyphens inside it), a run of whitespace, or
    // any other single character.
    [GeneratedRegex(@"<[^>]*>|&#?\w+;|[\p{L}\p{N}]+(?:['’\-][\p{L}\p{N}]+)*|\s+|.", RegexOptions.Singleline)]
    private static partial Regex TokenPattern();
}
