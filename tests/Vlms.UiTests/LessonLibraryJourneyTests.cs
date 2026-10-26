using Deque.AxeCore.Commons;
using Deque.AxeCore.Playwright;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using Vlms.Infrastructure;
using Vlms.UiTests.Capture;

namespace Vlms.UiTests;

/// <summary>
/// The lesson library (/lessons) in Chromium: each rank is headed by its name alone, once, with its medallion on the
/// same line; the lesson columns line up from one rank's table to the next; no horizontal scrolling at 375 px; and
/// axe-core (WCAG 2.2 A/AA) finds no violations.
/// </summary>
public sealed class LessonLibraryJourneyTests
{
    private static readonly string[] WcagTags = ["wcag2a", "wcag2aa", "wcag21aa", "wcag22aa"];

    [Fact]
    public async Task Each_rank_is_headed_by_its_name_beside_its_medallion_and_the_lesson_columns_line_up()
    {
        using var factory = new UiWebFactory();
        factory.StartServer();
        var baseUrl = factory.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First().TrimEnd('/');
        var personas = await CaptureSeed.SeedAsync(factory);

        IReadOnlyList<string> rankNames;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<VlmsDbContext>();
            rankNames = await db.Ranks.OrderBy(r => r.Order).Select(r => r.Name).ToListAsync();
        }

        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        await using var context = await browser.NewContextAsync(new BrowserNewContextOptions { Locale = "en-GB" });
        var page = await context.NewPageAsync();
        await DesignCapture.SignInAsync(page, baseUrl, personas["teacher"]);
        await page.GotoAsync(baseUrl + "/lessons", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });

        var rankHeadings = page.Locator("main").GetByRole(AriaRole.Heading, new() { Level = 2 });
        Assert.Equal(rankNames.Count, await rankHeadings.CountAsync());

        foreach (var name in rankNames)
        {
            // The accessible name is the rank name alone: no "Code: " in front of it, and the name appears once.
            var heading = page.Locator("main").GetByRole(AriaRole.Heading, new() { Level = 2, Name = name, Exact = true });
            await Assertions.Expect(heading).ToHaveCountAsync(1);

            var medallion = await heading.Locator("xpath=./span[contains(@class, 'medallion')]").BoundingBoxAsync();
            var label = await heading.Locator("xpath=./span[not(contains(@class, 'medallion'))]").BoundingBoxAsync();
            Assert.NotNull(medallion);
            Assert.NotNull(label);
            Assert.True(Math.Abs((medallion.Y + medallion.Height / 2) - (label.Y + label.Height / 2)) < 4,
                $"The medallion and the name for {name} are not on one line.");
        }

        var columnLefts = await page.EvaluateAsync<string[]>(
            "() => [...document.querySelectorAll('main table.lesson-table')].map(t => [...t.querySelectorAll('thead th')].map(th => Math.round(th.getBoundingClientRect().left)).join(','))");
        Assert.True(columnLefts.Length > 1, "Expected a lesson table for more than one rank.");
        Assert.True(columnLefts.All(c => c == columnLefts[0]),
            "The lesson columns do not line up from one rank to the next: " + string.Join(" | ", columnLefts));

        await AssertNoWcagViolationsAsync(page);

        await page.SetViewportSizeAsync(375, 800);
        Assert.False(await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth > window.innerWidth"),
            "The lesson library scrolls horizontally at 375 px.");
    }

    private static async Task AssertNoWcagViolationsAsync(IPage page)
    {
        var result = await page.RunAxe(new AxeRunOptions { RunOnly = new RunOnlyOptions { Type = "tag", Values = [.. WcagTags] } });
        Assert.True(result.Violations.Length == 0,
            "WCAG violations on the lesson library: " + string.Join("; ", result.Violations.Select(v => $"{v.Id} ({string.Join(", ", v.Nodes.Take(3).Select(n => n.Target))})")));
    }
}
