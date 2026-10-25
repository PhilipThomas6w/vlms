using System.Text;
using Deque.AxeCore.Commons;
using Deque.AxeCore.Playwright;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;

namespace Vlms.UiTests.Capture;

/// <summary>
/// Screenshots every page as each role at phone, tablet and desktop widths, with an axe-core run and a
/// horizontal-overflow check per page, into <c>artifacts/ui/capture/&lt;timestamp&gt;/</c> (gitignored) with an
/// <c>index.md</c>. Opt-in (<see cref="CaptureFactAttribute"/>): run with
/// <c>$env:VLMS_CAPTURE=1; dotnet test tests/Vlms.UiTests --filter "FullyQualifiedName~DesignCapture"</c>.
/// Optional <c>VLMS_CAPTURE_ONLY</c>: comma-separated persona names, or route fragments, to narrow the run.
/// </summary>
public sealed class DesignCapture
{
    private static readonly (string Name, int Width, int Height)[] Viewports =
    [
        ("phone", 375, 812),
        ("tablet", 768, 1024),
        ("desktop", 1280, 800),
    ];

    private static readonly string[] WcagTags = ["wcag2a", "wcag2aa", "wcag21aa", "wcag22aa"];

    /// <summary>Persona → pages. "anonymous" is not signed in.</summary>
    private static readonly (string Persona, string[] Paths)[] Plan =
    [
        ("anonymous", ["/", "/Account/Login", "/Account/Register", "/Account/ForgotPassword", "/Account/ResendEmailConfirmation", "/Account/ResetPassword?code=abc", "/Account/AccessDenied", "/not-found", "/guardianship/accept-invite"]),
        ("student-1", ["/", "/student", "/lessons", "/calendar"]),
        ("parent", ["/", "/calendar", "/calendar/events/1", "/parent/dashboard", "/parent/consent", "/parent/student-sign-ins", "/registration/apply", "/registration/my-applications", "/Account/Manage/ChangePassword"]),
        ("teacher", ["/", "/calendar", "/calendar/events/1", "/attendance/1/3", "/attendance", "/students", "/students/1", "/students/consent", "/students/1/consent", "/progress/record", "/progress/record#lesson", "/progress/completions", "/curriculum/teacher", "/lessons"]),
        ("approver", ["/", "/curriculum/approver"]),
        ("safeguarding", ["/", "/safeguarding/consent", "/safeguarding/dbs", "/registration/review"]),
        ("admin", ["/", "/calendar", "/calendar/events/new", "/calendar/events/5/edit", "/calendar/events/1", "/attendance", "/administration/students", "/administration/user-roles", "/administration/student-sign-ins", "/guardianship/links", "/registration/approve", "/registration/students", "/reporting", "/reporting/records-due-for-deletion", "/retention/mark-as-left", "/Account/Manage/TwoFactorAuthentication", "/Account/Manage/EnableAuthenticator"]),
        ("national", ["/", "/administration/chapters", "/administration/rank-badges", "/administration/saints", "/reporting/national", "/Error"]),
        ("admin-teacher", ["/"]),
    ];

    [CaptureFact]
    public async Task Capture_every_page_for_every_role()
    {
        using var factory = new UiWebFactory();
        factory.StartServer();
        var baseUrl = factory.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First().TrimEnd('/');
        var personas = await CaptureSeed.SeedAsync(factory);

        var outDir = Environment.GetEnvironmentVariable("VLMS_CAPTURE_DIR")
            ?? Path.Combine(Path.GetDirectoryName(CaptureSeed.FindRepoFile("Vlms.slnx"))!, "artifacts", "ui", "capture", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(outDir);
        var only = (Environment.GetEnvironmentVariable("VLMS_CAPTURE_ONLY") ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var index = new StringBuilder("# Design capture\n\n")
            .AppendLine($"{DateTime.Now:dd/MM/yyyy HH:mm}. Synthetic data (`CaptureSeed`). Axe tags: {string.Join(", ", WcagTags)}. Overflow = page wider than the viewport.\n")
            .AppendLine("| Persona | Path | Landed on | Axe violations (desktop) | Overflow |")
            .AppendLine("|---|---|---|---|---|");

        foreach (var (personaName, paths) in Plan)
        {
            var selected = paths.Where(p => only.Length == 0 || only.Contains(personaName) || only.Any(o => p.Contains(o, StringComparison.OrdinalIgnoreCase))).ToList();
            if (selected.Count == 0) continue;

            var personaDir = Path.Combine(outDir, personaName);
            Directory.CreateDirectory(personaDir);
            await using var context = await browser.NewContextAsync(new BrowserNewContextOptions { Locale = "en-GB" });
            var page = await context.NewPageAsync();
            if (personaName != "anonymous") await SignInAsync(page, baseUrl, personas[personaName]);

            foreach (var path in selected)
            {
                var slug = Slug(path);
                var landed = "";
                var axe = "";
                var overflow = new List<string>();
                foreach (var (vpName, width, height) in Viewports)
                {
                    await page.SetViewportSizeAsync(width, height);
                    await OpenAsync(page, baseUrl, path);
                    landed = new Uri(page.Url).PathAndQuery;
                    await page.ScreenshotAsync(new PageScreenshotOptions { Path = Path.Combine(personaDir, $"{slug}-{vpName}.png"), FullPage = true });
                    var wide = await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth > window.innerWidth + 1");
                    if (wide)
                    {
                        // Name the widest offenders: elements whose right edge passes the viewport.
                        var culprits = await page.EvaluateAsync<string>(@"() => [...document.querySelectorAll('body *')]
                            .map(e => ({ e, r: e.getBoundingClientRect().right }))
                            .filter(x => x.r > window.innerWidth + 1)
                            .sort((a, b) => b.r - a.r).slice(0, 3)
                            .map(x => x.e.tagName.toLowerCase() + (x.e.className && typeof x.e.className === 'string' ? '.' + x.e.className.trim().split(/\s+/).join('.') : '') + ' ' + Math.round(x.r))
                            .join('; ')");
                        overflow.Add($"{vpName} ({culprits})");
                    }
                    if (vpName == "desktop")
                    {
                        var result = await page.RunAxe(new AxeRunOptions { RunOnly = new RunOnlyOptions { Type = "tag", Values = [.. WcagTags] } });
                        axe = string.Join(", ", result.Violations.Select(v => $"{v.Id} ({v.Impact}, {v.Nodes.Length})"));
                    }
                }

                index.AppendLine($"| {personaName} | `{path}` | `{landed}` | {(axe.Length == 0 ? "none" : axe)} | {(overflow.Count == 0 ? "no" : string.Join(", ", overflow))} |");
            }
        }

        await File.WriteAllTextAsync(Path.Combine(outDir, "index.md"), index.ToString());
    }

    private static async Task OpenAsync(IPage page, string baseUrl, string path)
    {
        // "#lesson" is a capture-only marker: open Record completions with a lesson chosen.
        var url = path.Replace("#lesson", "");
        await page.GotoAsync(baseUrl + url, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
        await page.WaitForTimeoutAsync(400);
        if (path.EndsWith("#lesson", StringComparison.Ordinal))
        {
            // Shield-bearer, its sixth lesson, and the "select everyone ready" shortcut.
            await page.GetByLabel("Shield-bearer", new PageGetByLabelOptions { Exact = true }).CheckAsync();
            await page.WaitForTimeoutAsync(300);
            await page.Locator("input[name=lesson]").Nth(5).CheckAsync();
            await page.WaitForTimeoutAsync(500);
            var selectAll = page.Locator("button", new PageLocatorOptions { HasTextString = "Select everyone ready" });
            if (await selectAll.CountAsync() > 0 && await selectAll.IsEnabledAsync()) await selectAll.ClickAsync();
            await page.WaitForTimeoutAsync(400);
        }
    }

    internal static async Task SignInAsync(IPage page, string baseUrl, CapturePersona persona)
    {
        await page.GotoAsync(baseUrl + "/Account/Login", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
        await page.FillAsync("#username", persona.UserName);
        await page.FillAsync("#password", CaptureSeed.Password);
        await page.ClickAsync("button[type=submit]");
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        if (persona.AuthenticatorKey is not null)
        {
            await page.WaitForURLAsync("**/Account/LoginWith2fa**");
            await page.FillAsync("#code", CaptureSeed.Totp(persona.AuthenticatorKey));
            await page.ClickAsync("button[type=submit]");
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        }

        if (page.Url.Contains("/Account/", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Sign-in for {persona.Name} ended on {page.Url}");
        }
    }

    private static string Slug(string path)
    {
        var slug = path.Trim('/').Replace('/', '_').Replace("#", "__").Replace('?', '_').Replace('=', '_');
        return slug.Length == 0 ? "home" : slug;
    }
}
