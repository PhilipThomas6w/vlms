using Vlms.Infrastructure.Curriculum;
using Xunit;

namespace Vlms.Tests.Infrastructure;

/// <summary>
/// <see cref="LessonDiff"/> (docs/adr/0013 decision 4): word-level tracked changes as &lt;ins&gt;/&lt;del&gt; in the
/// rendered lesson, so differences in how the Markdown is spelt are not shown as changes, and pictures compared by content.
/// </summary>
public sealed class LessonDiffTests
{
    private static LessonDiff.Side Side(string body) => new(body, new Dictionary<string, (string, string)>());

    [Fact]
    public void ChangedWords_AreMarkedAddedAndRemoved_AndTheRestLeftAlone()
    {
        var html = LessonDiff.Tracked(Side("Why was St John given the name?\n"), Side("Why was St John given the name Chrysostom?\n"));

        Assert.Contains("<ins>", html);
        Assert.Contains("Chrysostom", html);
        Assert.StartsWith("<p>Why was St John given the name", html);
    }

    [Fact]
    public void ARemovedWord_IsStruckThrough()
    {
        var html = LessonDiff.Tracked(Side("The golden mouth.\n"), Side("The mouth.\n"));

        Assert.Contains("<del>golden", html);
        Assert.DoesNotContain("<ins>", html);
    }

    [Fact]
    public void AnAddedListItem_KeepsItsListStructure()
    {
        var html = LessonDiff.Tracked(Side("- One\n"), Side("- One\n- Two\n"));

        Assert.Contains("<li>", html);
        Assert.Contains("<ins>Two</ins>", html.Replace("\n", ""));
    }

    [Fact]
    public void TheSameTextSpeltDifferently_IsNoChange()
    {
        var html = LessonDiff.Tracked(Side("*Italic* text\n"), Side("_Italic_ text\n"));

        Assert.DoesNotContain("<ins>", html);
        Assert.DoesNotContain("<del>", html);
    }

    [Fact]
    public void UnchangedText_HasNoMarks()
    {
        var html = LessonDiff.Tracked(Side("## Questions\n\n- Why?\n"), Side("## Questions\n\n- Why?\n"));

        Assert.DoesNotContain("<ins>", html);
        Assert.DoesNotContain("<del>", html);
    }

    [Fact]
    public void RawHtml_InTheText_IsNotRenderedAsMarkup()
    {
        var html = LessonDiff.Tracked(Side("Text\n"), Side("Text <script>alert(1)</script>\n"));

        Assert.DoesNotContain("<script>", html);
    }
}
