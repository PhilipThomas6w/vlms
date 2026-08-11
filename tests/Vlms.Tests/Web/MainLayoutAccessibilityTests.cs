using System.IO;
using Xunit;

namespace Vlms.Tests.Web;

/// <summary>
/// Regression coverage for the WCAG 2.2 1.1.1 (Non-text Content) gap closed in STATE.md's
/// "MainLayout.razor dismiss-glyph missing aria-label" item. docs/quality/wcag-2.2-aa-checklist.md
/// flagged this at authoring time: Layout/MainLayout.razor's `#blazor-error-ui` dismiss control was
/// a bare emoji glyph, styled `cursor: pointer` in MainLayout.razor.css and wired to hide the
/// banner by Blazor's own boot script — Blazor-template-default markup, not authored by this
/// project — with no accessible name, so a screen reader would announce either nothing or the raw
/// glyph rather than something usable.
///
/// Same lightweight markup-content-assertion convention as PwaAssetsTests.cs/
/// StatusMessageAccessibilityTests.cs — reads the .razor file directly off disk, no browser/
/// axe-core, per ADR-0003's "fewest moving parts" reasoning and this project's established
/// rejection of browser-driven accessibility tooling for a solo build.
/// </summary>
public class MainLayoutAccessibilityTests
{
    [Fact]
    public void DismissGlyph_HasAriaLabel()
    {
        var path = Path.Combine(RepoRoot(), "src", "Vlms.Web", "Components", "Layout", "MainLayout.razor");
        Assert.True(File.Exists(path), $"Expected a layout at {path}");

        var content = File.ReadAllText(path);

        Assert.Contains("class=\"dismiss\"", content);
        Assert.Contains("aria-label=\"Dismiss\"", content);
    }

    /// <summary>
    /// Walks up from the test assembly's output directory to find the repository root
    /// (identified by Vlms.slnx), since this suite reads Vlms.Web's markup directly off disk
    /// rather than via a project reference (Vlms.Tests doesn't reference Vlms.Web) — same
    /// approach as PwaAssetsTests/StatusMessageAccessibilityTests.
    /// </summary>
    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Vlms.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.True(directory is not null, "Could not locate repository root (Vlms.slnx) from test output directory");
        return directory!.FullName;
    }
}
