using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace Vlms.Tests.Web;

/// <summary>
/// FR-012 / TC-022: every routable user-facing page is reachable from the navigation for the roles
/// its policy allows, and no nav or home link is shown to a role the page refuses. Reads the
/// .razor sources directly (same convention as MainLayoutAccessibilityTests).
/// </summary>
public class NavigationCoverageTests
{
    // Roles each policy admits (Program.cs). Keep in step with the policy definitions there.
    private static readonly Dictionary<string, string[]> PolicyRoles = new()
    {
        ["RequireAdmin"] = ["Admin"],
        ["RequireTeacher"] = ["Teacher"],
        ["RequireApprover"] = ["Approver"],
        ["RequireLessonSuggester"] = ["Teacher", "Approver"],
        ["RequireLessonHistory"] = ["Approver", "Admin"],
        ["RequireParent"] = ["Parent"],
        ["RequireStudent"] = ["Student"],
        ["RequireSafeguardingOfficer"] = ["SafeguardingOfficer"],
        ["RequireNationalAdministrator"] = ["NationalAdministrator"],
        ["RequireAdminOrTeacher"] = ["Admin", "Teacher"],
        ["RequireAttendanceReader"] = ["Admin", "Teacher", "SafeguardingOfficer"],
        ["RequireAdminOrSafeguardingOfficer"] = ["Admin", "SafeguardingOfficer"],
        ["RequireChapterMember"] = ["Admin", "Teacher", "SafeguardingOfficer", "Parent", "Student"],
        ["RequireLessonLibrary"] = ["Teacher", "Approver", "Admin", "Student", "Parent", "NationalAdministrator"],
        // Any signed-in user, including one with no role yet (parent registration, adr/0008).
        ["RequireSignedIn"] = ["Admin", "Teacher", "Approver", "Parent", "Student", "SafeguardingOfficer", "NationalAdministrator"],
    };

    // Account, error and not-found pages are not navigation targets; AcceptInvite is reached from an emailed link.
    private static readonly string[] ExcludedPrefixes = ["/Account/", "/Error", "/not-found", "/guardianship/accept-invite"];

    // Reached from list pages, not the nav.
    private static readonly string[] ExcludedRoutes =
    [
        "/students/{StudentId:int}", "/students/{StudentId:int}/consent",
        // Reached from the calendar: an event, its form, each class's register for it, and an excursion's one register.
        "/calendar/events/new", "/calendar/events/{EventId:int}", "/calendar/events/{EventId:int}/edit",
        "/attendance/{EventId:int}/{RankId:int}", "/attendance/{EventId:int}/everyone",
        // A converted lesson (adr/0012), reached from the lesson library.
        "/lessons/{Code}",
        // Lesson suggestions (adr/0013): the editor and history from the lesson page, a new lesson from the library,
        // and one suggestion from Changes to review.
        "/lessons/{Code}/suggest", "/lessons/new/{RankCode}", "/lessons/{Code}/history", "/curriculum/review/{Id:int}",
    ];

    // Routes linked ahead of their page being merged; empty since /student merged (FR-011).
    private static readonly string[] PendingRoutes = [];

    [Fact]
    public void EveryRoutablePage_HasANavLink_WithTheSamePolicy()
    {
        var navLinks = ReadLinks("Layout/NavMenu.razor");
        var missing = new List<string>();

        foreach (var (route, policy) in Pages())
        {
            Assert.True(policy is not null, $"{route} has no [Authorize(Policy=...)]; extend this test if that is intentional");
            if (!navLinks.Any(l => l.Href == route && l.Policy == policy))
                missing.Add($"{route} ({policy})");
        }

        Assert.True(missing.Count == 0, "Pages with no nav link under their own policy: " + string.Join(", ", missing));
    }

    [Theory]
    [InlineData("Layout/NavMenu.razor")]
    [InlineData("Pages/Home.razor")]
    public void Links_PointAtRealPages_AndAreOnlyShownToRolesThePageAllows(string file)
    {
        var pages = Pages().ToDictionary(p => p.Route, p => p.Policy);
        var problems = new List<string>();

        foreach (var (href, policy) in ReadLinks(file))
        {
            if (href == "/" || policy is null && href.StartsWith("/Account/", StringComparison.Ordinal)) continue;
            if (PendingRoutes.Contains(href)) continue;
            if (!pages.TryGetValue(href, out var pagePolicy)) { problems.Add($"{href}: no such page"); continue; }
            if (policy is null) { problems.Add($"{href}: link outside any AuthorizeView policy"); continue; }
            if (!PolicyRoles[policy].All(PolicyRoles[pagePolicy!].Contains))
                problems.Add($"{href}: shown under {policy} but page requires {pagePolicy}");
        }

        Assert.True(problems.Count == 0, string.Join("; ", problems));
    }

    [Fact]
    public void NavMenu_IsALabelledNav_WithAriaCurrent_AndLayoutKeepsAccountLinks()
    {
        var nav = File.ReadAllText(Path.Combine(ComponentsRoot(), "Layout/NavMenu.razor"));
        Assert.Contains("<nav aria-label=\"Main\">", nav);
        Assert.Contains("aria-current=", nav);

        var layout = File.ReadAllText(Path.Combine(ComponentsRoot(), "Layout/MainLayout.razor"));
        Assert.Contains("<NavMenu />", layout);
        Assert.Contains("Account/Manage/TwoFactorAuthentication", layout);
        Assert.Contains("Account/Logout", layout);
    }

    private static IEnumerable<(string Route, string? Policy)> Pages()
    {
        foreach (var file in Directory.GetFiles(ComponentsRoot(), "*.razor", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            var routes = Regex.Matches(text, @"(?m)^\s*@page\s+""([^""]+)""").Select(m => m.Groups[1].Value).ToList();
            if (routes.Count == 0) continue;
            var policy = Regex.Match(text, @"@attribute\s*\[Authorize\(Policy\s*=\s*""(\w+)""\)\]") is { Success: true } m ? m.Groups[1].Value : null;

            foreach (var route in routes)
            {
                if (route == "/" || ExcludedRoutes.Contains(route)) continue;
                if (ExcludedPrefixes.Any(p => route.StartsWith(p, StringComparison.OrdinalIgnoreCase))) continue;
                yield return (route, policy);
            }
        }
    }

    /// <summary>Each href with the policy of its innermost enclosing AuthorizeView (null when none).</summary>
    private static List<(string Href, string? Policy)> ReadLinks(string relativePath)
    {
        var text = File.ReadAllText(Path.Combine(ComponentsRoot(), relativePath));
        var result = new List<(string, string?)>();
        var stack = new Stack<string?>();

        foreach (Match m in Regex.Matches(text, @"<AuthorizeView(?<attrs>[^>]*)>|</AuthorizeView>|<a\s+href=""(?<href>[^""]+)"""))
        {
            if (m.Value.StartsWith("</", StringComparison.Ordinal)) { stack.Pop(); continue; }
            if (m.Value.StartsWith("<AuthorizeView", StringComparison.Ordinal))
            {
                var p = Regex.Match(m.Groups["attrs"].Value, @"Policy=""(\w+)""");
                // Policy-less AuthorizeView (signed in / signed out) inherits the surrounding policy.
                stack.Push(p.Success ? p.Groups[1].Value : stack.Count > 0 ? stack.Peek() : null);
                continue;
            }
            result.Add((m.Groups["href"].Value, stack.Count > 0 ? stack.Peek() : null));
        }

        return result;
    }

    private static string ComponentsRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Vlms.slnx")))
            directory = directory.Parent;
        Assert.True(directory is not null, "Could not locate repository root (Vlms.slnx)");
        return Path.Combine(directory!.FullName, "src", "Vlms.Web", "Components");
    }
}
