using System.Text;
using System.Text.Json;
using Deque.AxeCore.Commons;
using Deque.AxeCore.Playwright;
using Microsoft.Playwright;

namespace Vlms.UiTests;

/// <summary>
/// Browser checks on the pages an anonymous visitor can reach: axe-core against WCAG 2.0/2.1/2.2 A and AA
/// (the rules that need a rendered page, such as colour contrast, 1.4.3 and 1.4.11) and reflow at
/// 320 CSS px (1.4.10). <c>Baseline/known-violations.json</c> lists, per page, the violations that
/// already existed when these tests were added (docs/adr/0009). A violation not in the baseline fails the
/// test; a baseline entry that no longer reproduces also fails, so the list shrinks as the redesign fixes things.
/// </summary>
[Collection(BrowserCollection.Name)]
public sealed class PublicPageAccessibilityTests(BrowserFixture fixture)
{
    private static readonly string[] WcagTags = ["wcag2a", "wcag2aa", "wcag21aa", "wcag22aa"];

    /// <summary>Every page reachable without signing in. Pages that need a token or session state
    /// (confirm email, 2FA, accept invite) are not reachable by URL alone and are not listed.</summary>
    public static TheoryData<string> PublicPages =>
    [
        "/",
        "/Account/Login",
        "/Account/Register",
        "/Account/ForgotPassword",
        "/Account/ResendEmailConfirmation",
        "/Account/ResetPassword?code=ui-test",
        "/not-found",
    ];

    private static readonly Lazy<KnownViolations> KnownBaseline = new(KnownViolations.Load);

    [Theory]
    [MemberData(nameof(PublicPages))]
    public async Task Page_has_no_wcag_violations_beyond_the_recorded_baseline(string path)
    {
        var page = await fixture.NewPageAsync();
        try
        {
            await page.GotoAsync(fixture.BaseUrl + path, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });

            var result = await page.RunAxe(new AxeRunOptions { RunOnly = new RunOnlyOptions { Type = "tag", Values = [.. WcagTags] } });

            var found = result.Violations.Select(v => v.Id).Distinct().ToHashSet(StringComparer.Ordinal);
            var known = KnownBaseline.Value.Axe.GetValueOrDefault(path)?.ToHashSet(StringComparer.Ordinal) ?? [];

            var added = found.Except(known).Order().ToList();
            var gone = known.Except(found).Order().ToList();

            var message = new StringBuilder();
            if (added.Count > 0)
            {
                message.AppendLine($"New WCAG violations on {path} (not in Baseline/known-violations.json):");
                foreach (var violation in result.Violations.Where(v => added.Contains(v.Id)))
                {
                    message.AppendLine($"  {violation.Id} [{violation.Impact}] {violation.Help} ({violation.Nodes.Length} node(s))");
                    foreach (var node in violation.Nodes.Take(5)) message.AppendLine($"    {node.Target}");
                }
            }
            if (gone.Count > 0)
            {
                message.AppendLine($"Baseline lists violations on {path} that no longer occur; remove them from Baseline/known-violations.json: {string.Join(", ", gone)}");
            }

            Assert.True(message.Length == 0, message.ToString());
        }
        finally
        {
            await page.Context.CloseAsync();
        }
    }

    [Theory]
    [MemberData(nameof(PublicPages))]
    public async Task Page_reflows_at_320_css_pixels_without_horizontal_scrolling(string path)
    {
        var page = await fixture.NewPageAsync(width: 320, height: 800);
        try
        {
            await page.GotoAsync(fixture.BaseUrl + path, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });

            var scrollWidth = await page.EvaluateAsync<int>("() => document.documentElement.scrollWidth");
            var innerWidth = await page.EvaluateAsync<int>("() => window.innerWidth");
            var overflows = scrollWidth > innerWidth;
            var known = KnownBaseline.Value.Reflow320.Contains(path, StringComparer.Ordinal);

            Assert.False(overflows && !known,
                $"{path} scrolls horizontally at 320 px (scrollWidth {scrollWidth} > innerWidth {innerWidth}). WCAG 1.4.10.");
            Assert.False(!overflows && known,
                $"{path} now reflows at 320 px; remove it from reflow320 in Baseline/known-violations.json.");
        }
        finally
        {
            await page.Context.CloseAsync();
        }
    }

    private sealed record KnownViolations(Dictionary<string, string[]> Axe, string[] Reflow320)
    {
        public static KnownViolations Load()
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Baseline", "known-violations.json");
            return JsonSerializer.Deserialize<KnownViolations>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                   ?? throw new InvalidOperationException("Baseline/known-violations.json is empty.");
        }
    }
}
