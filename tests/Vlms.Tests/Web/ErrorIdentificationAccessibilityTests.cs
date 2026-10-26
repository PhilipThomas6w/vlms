using System.IO;
using Xunit;

namespace Vlms.Tests.Web;

/// <summary>
/// Regression coverage for the WCAG 2.2 "Error identification (3.3.1) / suggestion (3.3.3)"
/// checklist review recorded in STATE.md and docs/quality/wcag-2.2-aa-checklist.md.
///
/// This review re-confirmed, rather than assumed, that every page's inline
/// &lt;p class="error" role="alert"&gt;@_errorMessage&lt;/p&gt; element (the RegisterStudent.razor
/// pattern) genuinely carries role="alert" on every current page, plus checked the assumption this
/// checklist item's own prior framing had never verified: whether Blazor's built-in
/// &lt;ValidationSummary /&gt; component renders with role="alert" by default. Reading
/// Microsoft.AspNetCore.Components.Forms.ValidationSummary's actual source
/// (dotnet/aspnetcore, src/Components/Web/src/Forms/ValidationSummary.cs) confirmed it does not —
/// it renders a bare &lt;ul class="validation-errors"&gt; with no role/aria-live attribute at all,
/// relying entirely on AdditionalAttributes (i.e. whatever extra attributes are written on the
/// &lt;ValidationSummary&gt; tag in Razor markup) to supply one.
///
/// That assumption check found a real, confirmed defect: six pages under
/// src/Vlms.Web/Components/Pages/ (TeacherProposals.razor, since removed, GuardianLinks.razor,
/// CompletionReversals.razor, RegisterStudent.razor, DbsChecks.razor, ConsentRecords.razor) used a
/// bare &lt;ValidationSummary /&gt; with no role="alert" — client-side validation errors listed
/// there were never announced to assistive tech, unlike every one of the six Account pages
/// (Login.razor, Register.razor, LoginWith2fa.razor, LoginWithRecoveryCode.razor,
/// Manage/EnableAuthenticator.razor) and Guardianship/AcceptInvite.razor, all of which already
/// wrote &lt;ValidationSummary role="alert" /&gt; explicitly. Fixed by adding role="alert" to the
/// six missing pages, bringing every &lt;ValidationSummary /&gt; in the codebase into line with the
/// pattern the Account pages had already established.
///
/// Also covers the two framework/template-default surfaces this pass checked per the lesson from
/// the immediately-prior "Keyboard operability" checklist item's checker-FAIL round-trip (STATE.md):
/// a broad review must cover framework-default chrome, not just the app's own named form pages.
/// - Layout/MainLayout.razor's #blazor-error-ui banner ("An unhandled error has occurred.") had no
///   role/aria-live of its own — confirmed against the real dotnet/aspnetcore Blazor Web App
///   project template (src/ProjectTemplates/.../MainLayout.razor), which ships this element with no
///   role either, so this was a genuine gap in the template default, not something this project's
///   own prior edits (the aria-label/button fixes) had introduced or already covered. Fixed by
///   adding role="alert" directly to the #blazor-error-ui div.
/// - Layout/ReconnectModal.razor's genuine failure-state messages ("Failed to rejoin...",
///   "Failed to resume the session...") are revealed by toggling a CSS class on the modal's already
///   -open &lt;dialog&gt; (ReconnectModal.razor.js's handleReconnectStateChanged calls showModal()
///   once, then blazor's own boot script toggles state classes on the same open dialog) — a screen
///   reader that already announced the dialog on open has no reason to notice a later CSS-only
///   content swap inside it. Fixed by adding role="alert" to the two failure-state &lt;p&gt;
///   elements specifically (not the "Rejoining..."/"paused"/countdown states, which are advisory,
///   not failures, and in the countdown's case would make role="alert" re-announce every second) —
///   the same "advisory status vs. must-interrupt failure" distinction the "Status messages (4.1.3)"
///   item already established for this codebase.
///
/// Same lightweight markup-content-assertion convention as PwaAssetsTests.cs/
/// StatusMessageAccessibilityTests.cs/MainLayoutAccessibilityTests.cs — reads files directly off
/// disk, no browser/axe-core, per ADR-0003's "fewest moving parts" reasoning.
/// </summary>
public class ErrorIdentificationAccessibilityTests
{
    [Theory]
    [InlineData("Guardianship", "GuardianLinks.razor")]
    [InlineData("Progress", "CompletionReversals.razor")]
    [InlineData("Registration", "RegisterStudent.razor")]
    [InlineData("Safeguarding", "DbsChecks.razor")]
    [InlineData("Safeguarding", "ConsentRecords.razor")]
    [InlineData("Guardianship", "AcceptInvite.razor")]
    public void Page_ValidationSummary_HasRoleAlert(string folder, string fileName)
    {
        var content = ReadPage(folder, fileName);

        Assert.Contains("<ValidationSummary role=\"alert\" />", content);
        Assert.DoesNotContain("<ValidationSummary />", content);
    }

    [Theory]
    [InlineData("Guardianship", "GuardianLinks.razor")]
    [InlineData("Guardianship", "AcceptInvite.razor")]
    [InlineData("Registration", "RegisterStudent.razor")]
    [InlineData("Safeguarding", "ConsentRecords.razor")]
    [InlineData("Safeguarding", "DbsChecks.razor")]
    [InlineData("Progress", "CompletionReversals.razor")]
    [InlineData("Retention", "MarkAsLeft.razor")]
    [InlineData("Reporting", "RecordsDueForDeletion.razor")]
    [InlineData("Reporting", "ProgressReporting.razor")]
    [InlineData("Parent", "ParentDashboard.razor")]
    public void Page_InlineErrorParagraph_HasRoleAlert(string folder, string fileName)
    {
        var content = ReadPage(folder, fileName);

        Assert.Contains("<p class=\"error\" role=\"alert\">@_errorMessage</p>", content);
    }

    [Fact]
    public void MainLayout_BlazorErrorUiBanner_HasRoleAlert()
    {
        var content = ReadLayoutFile("MainLayout.razor");

        Assert.Contains("<div id=\"blazor-error-ui\" role=\"alert\" data-nosnippet>", content);
    }

    [Fact]
    public void ReconnectModal_FailureStateMessages_HaveRoleAlert()
    {
        var content = ReadLayoutFile("ReconnectModal.razor");

        Assert.Contains("<p class=\"components-reconnect-failed-visible\" role=\"alert\">", content);
        Assert.Contains("<p class=\"components-resume-failed-visible\" role=\"alert\">", content);
    }

    [Fact]
    public void ReconnectModal_AdvisoryStateMessages_AreNotOverAnnounced()
    {
        // Deliberate exclusion, not an oversight: the "Rejoining..."/repeated-attempt countdown/
        // "paused" states are advisory, not failures, and the countdown text updates every second —
        // giving it role="alert" would re-announce on every tick. Only the two genuine failure
        // states above got role="alert", matching the "advisory status vs. must-interrupt failure"
        // split the "Status messages (4.1.3)" item already established.
        var content = ReadLayoutFile("ReconnectModal.razor");

        Assert.Contains("<p class=\"components-reconnect-first-attempt-visible\">", content);
        Assert.Contains("<p class=\"components-reconnect-repeated-attempt-visible\">", content);
        Assert.Contains("<p class=\"components-pause-visible\">", content);
    }

    [Fact]
    public void Routes_NotAuthorizedMessage_HasRoleAlert()
    {
        var path = Path.Combine(RepoRoot(), "src", "Vlms.Web", "Components", "Routes.razor");
        Assert.True(File.Exists(path), $"Expected a file at {path}");
        var content = File.ReadAllText(path);

        Assert.Contains("<p role=\"alert\">You are not authorized to view this page.</p>", content);
    }

    private static string ReadPage(string folder, string fileName)
    {
        var path = Path.Combine(RepoRoot(), "src", "Vlms.Web", "Components", "Pages", folder, fileName);
        Assert.True(File.Exists(path), $"Expected a page at {path}");
        return File.ReadAllText(path);
    }

    private static string ReadLayoutFile(string fileName)
    {
        var path = Path.Combine(RepoRoot(), "src", "Vlms.Web", "Components", "Layout", fileName);
        Assert.True(File.Exists(path), $"Expected a layout file at {path}");
        return File.ReadAllText(path);
    }

    /// <summary>
    /// Walks up from the test assembly's output directory to find the repository root
    /// (identified by Vlms.slnx), since this suite reads Vlms.Web's markup directly off disk
    /// rather than via a project reference (Vlms.Tests doesn't reference Vlms.Web) — same
    /// approach as PwaAssetsTests/StatusMessageAccessibilityTests/MainLayoutAccessibilityTests.
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
