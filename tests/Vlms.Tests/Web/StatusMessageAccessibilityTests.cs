using System.IO;
using Xunit;

namespace Vlms.Tests.Web;

/// <summary>
/// Regression coverage for the WCAG 2.2 "Status messages (4.1.3)" gap closed in STATE.md's
/// "WCAG 'Status messages (4.1.3)' gap" item. Before this item, none of the seven older
/// list-then-form pages (docs/quality/wcag-2.2-aa-checklist.md's own count — TeacherProposals,
/// ApproverProposals, GuardianLinks, AcceptInvite, RegisterStudent, ConsentRecords, DbsChecks)
/// surfaced a perceivable confirmation of a successful form submission — the form silently reset
/// and the list reloaded with no announcement to assistive tech. The eighth page,
/// Progress/CompletionReversals.razor, had the identical gap despite STATE.md's own "In progress"
/// item text (inaccurately) describing it as already fixed — confirmed by direct inspection
/// before this item touched anything, and fixed here alongside the seven.
///
/// The established pattern (Account/Pages/Manage/TwoFactorAuthentication.razor's recovery-code
/// regeneration message, the one page in this codebase that already had it) is a
/// &lt;p role="status"&gt; element that only renders once a `_statusMessage` field is set on
/// success, mirroring the existing `_errorMessage`/role="alert" pattern used everywhere in this
/// codebase. This suite is a lightweight markup-content assertion (the same convention as
/// PwaAssetsTests.cs — no browser/axe-core, per ADR-0003's "fewest moving parts" reasoning and
/// this project's established rejection of browser-driven accessibility tooling for a solo build)
/// checking that each page's markup actually declares the role="status" element and that its
/// code-behind sets `_statusMessage` on every successful action path, not just that the field
/// exists.
///
/// Guardianship/AcceptInvite.razor is deliberately excluded — its success path redirects
/// (`RedirectManager.RedirectTo("/parent/dashboard")`) rather than staying on the page with a
/// silently-reset form, the same "success navigates away" shape already accepted as needing no
/// in-page status message for Account/Pages/Login.razor and friends. There is nothing to assert a
/// role="status" element against here because the page is left entirely on success.
/// </summary>
public class StatusMessageAccessibilityTests
{
    [Theory]
    [InlineData("Curriculum", "TeacherProposals.razor")]
    [InlineData("Curriculum", "ApproverProposals.razor")]
    [InlineData("Guardianship", "GuardianLinks.razor")]
    [InlineData("Registration", "RegisterStudent.razor")]
    [InlineData("Safeguarding", "ConsentRecords.razor")]
    [InlineData("Safeguarding", "DbsChecks.razor")]
    [InlineData("Progress", "CompletionReversals.razor")]
    [InlineData("Administration", "UserRoles.razor")]
    public void Page_DeclaresStatusRoleElement_BoundToAStatusMessageField(string folder, string fileName)
    {
        var content = ReadPage(folder, fileName);

        Assert.Contains("role=\"status\"", content);
        Assert.Contains("_statusMessage", content);

        // The role="status" element itself must actually render the field, not just declare it
        // unused elsewhere in the file (e.g. a stray comment) — matches this codebase's existing
        // <p class="error" role="alert">@_errorMessage</p> shape.
        Assert.Contains("<p role=\"status\">@_statusMessage</p>", content);
    }

    [Theory]
    [InlineData("Curriculum", "TeacherProposals.razor", "_statusMessage = ")]
    [InlineData("Curriculum", "ApproverProposals.razor", "_statusMessage = \"Proposal approved.\"")]
    [InlineData("Curriculum", "ApproverProposals.razor", "_statusMessage = \"Proposal rejected.\"")]
    [InlineData("Guardianship", "GuardianLinks.razor", "_statusMessage = \"Guardian link created.\"")]
    [InlineData("Registration", "RegisterStudent.razor", "_statusMessage = \"Student registered.\"")]
    [InlineData("Safeguarding", "ConsentRecords.razor", "_statusMessage = \"Consent record created.\"")]
    [InlineData("Safeguarding", "DbsChecks.razor", "_statusMessage = \"DBS check recorded.\"")]
    [InlineData("Progress", "CompletionReversals.razor", "_statusMessage = \"Completion reversed.\"")]
    [InlineData("Administration", "UserRoles.razor", "_statusMessage = $\"{role} granted to {user.DisplayName}.\"")]
    public void Page_SetsAConcreteStatusMessage_OnASuccessPath(string folder, string fileName, string expectedAssignment)
    {
        var content = ReadPage(folder, fileName);
        Assert.Contains(expectedAssignment, content);
    }

    [Fact]
    public void AcceptInvite_HasNoStatusRoleElement_BecauseSuccessRedirectsAway()
    {
        // Documents the deliberate exclusion (see class doc comment) rather than leaving it silently
        // unaddressed — if a future change makes AcceptInvite stay on the page after success, this
        // assertion is the tripwire that says the "redirect away" assumption no longer holds and the
        // status-message gap needs revisiting for this page too.
        var content = ReadPage("Guardianship", "AcceptInvite.razor");

        Assert.Contains("RedirectManager.RedirectTo(\"/parent/dashboard\")", content);
        Assert.DoesNotContain("role=\"status\"", content);
    }

    private static string ReadPage(string folder, string fileName)
    {
        var path = Path.Combine(RepoRoot(), "src", "Vlms.Web", "Components", "Pages", folder, fileName);
        Assert.True(File.Exists(path), $"Expected a page at {path}");
        return File.ReadAllText(path);
    }

    /// <summary>
    /// Walks up from the test assembly's output directory to find the repository root (identified
    /// by Vlms.slnx), since this suite reads Vlms.Web's markup directly off disk rather than via a
    /// project reference (Vlms.Tests doesn't reference Vlms.Web) — same approach as PwaAssetsTests.
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
