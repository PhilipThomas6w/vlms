using System.IO;
using Xunit;

namespace Vlms.Tests.Web;

/// <summary>
/// Regression coverage for two WCAG 2.2 gaps closed against Layout/MainLayout.razor's
/// `#blazor-error-ui` dismiss control, both recorded in STATE.md:
///
/// 1. "MainLayout.razor dismiss-glyph missing aria-label" (1.1.1 Non-text Content) — the control
///    was a bare emoji glyph with no accessible name; fixed by adding aria-label="Dismiss".
/// 2. The checker-FAIL round-trip fix to the "Keyboard operability (2.1.1) / focus order (2.4.3)"
///    checklist item — the control was a non-interactive `&lt;span&gt;`, styled `cursor: pointer` in
///    MainLayout.razor.css and wired to hide the banner by Blazor's own boot script (BootErrors.ts's
///    `document.querySelectorAll('#blazor-error-ui .dismiss')`, a pure class selector with no
///    element-type assumption), but a `&lt;span&gt;` never enters the keyboard tab order regardless of
///    CSS cursor styling — a genuine 2.1.1 failure a keyboard-only user could not work around (the
///    adjacent "Reload" `&lt;a&gt;` was reachable, "Dismiss" was not). Fixed by changing the element to a
///    real `&lt;button type="button"&gt;`, which Blazor's class-selector-based click wiring still finds
///    unchanged, and which gets native focus/Tab-order/Enter-Space activation for free.
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
        var content = ReadMainLayout();

        Assert.Contains("class=\"dismiss\"", content);
        Assert.Contains("aria-label=\"Dismiss\"", content);
    }

    [Fact]
    public void DismissControl_IsARealButton_NotASpan()
    {
        var content = ReadMainLayout();

        Assert.Contains("<button type=\"button\" class=\"dismiss\" aria-label=\"Dismiss\">", content);
        Assert.DoesNotContain("<span class=\"dismiss\"", content);
    }

    private static string ReadMainLayout()
    {
        var path = Path.Combine(RepoRoot(), "src", "Vlms.Web", "Components", "Layout", "MainLayout.razor");
        Assert.True(File.Exists(path), $"Expected a layout at {path}");

        return File.ReadAllText(path);
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
