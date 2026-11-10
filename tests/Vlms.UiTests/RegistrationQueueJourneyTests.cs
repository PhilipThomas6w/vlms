using Deque.AxeCore.Commons;
using Deque.AxeCore.Playwright;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using Vlms.Domain;
using Vlms.Infrastructure;
using Vlms.Infrastructure.Identity;
using Vlms.UiTests.Capture;

namespace Vlms.UiTests;

/// <summary>
/// The registration queue (adr/0017) in Chromium against the real app: someone who registered with
/// no role sees only that their registration is in progress and no menu but Home; the Admin opens
/// Registrations from the counted menu link, grants two roles at once with a confirmation, and the
/// person leaves the queue. Every page passes axe-core (WCAG 2.2 A/AA) and does not scroll sideways
/// on a phone.
/// </summary>
public sealed class RegistrationQueueJourneyTests
{
    private static readonly string[] WcagTags = ["wcag2a", "wcag2aa", "wcag21aa", "wcag22aa"];
    private const int NewcomerId = 950;
    private const string NewcomerName = "Tom Newcomer";
    private const string NewcomerEmail = "newcomer@capture.test";

    private static async Task AssertNoWcagViolationsAsync(IPage page, string where)
    {
        var result = await page.RunAxe(new AxeRunOptions { RunOnly = new RunOnlyOptions { Type = "tag", Values = [.. WcagTags] } });
        Assert.True(result.Violations.Length == 0,
            $"WCAG violations on {where}: " + string.Join("; ", result.Violations.Select(v => $"{v.Id} ({string.Join(", ", v.Nodes.Take(3).Select(n => n.Target))})")));
    }

    private static async Task AssertNoSidewaysScrollAsync(IPage page, string where)
    {
        var overflow = await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth > document.documentElement.clientWidth");
        Assert.False(overflow, $"{where} scrolls sideways at phone width.");
    }

    private static async Task<IPage> SignInAsync(IBrowser browser, string baseUrl, CapturePersona persona)
    {
        var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            Locale = "en-GB", ViewportSize = new ViewportSize { Width = 375, Height = 812 }
        });
        var page = await context.NewPageAsync();
        await DesignCapture.SignInAsync(page, baseUrl, persona);
        return page;
    }

    // Someone who registered for the chapter as a teacher, with no role yet.
    private static async Task<CapturePersona> AddNewcomerAsync(UiWebFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<VlmsIdentityUser>>();
        var created = await users.CreateAsync(
            new VlmsIdentityUser { Id = NewcomerId, UserName = NewcomerEmail, Email = NewcomerEmail, EmailConfirmed = true }, CaptureSeed.Password);
        Assert.True(created.Succeeded, string.Join("; ", created.Errors.Select(e => e.Description)));
        var db = scope.ServiceProvider.GetRequiredService<VlmsDbContext>();
        db.AppUsers.Add(new AppUser { Id = NewcomerId, DisplayName = NewcomerName, Email = NewcomerEmail });
        db.ChapterJoinRequests.Add(new ChapterJoinRequest
        {
            UserId = NewcomerId, ChapterId = Chapter.ShrewsburyId, JoiningAs = JoiningAs.Teacher, RequestedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
        return new CapturePersona(NewcomerEmail, NewcomerEmail, null, NewcomerId);
    }

    // A click made before the page's live connection is ready can be lost, so click again until the result appears.
    private static async Task ClickUntilAsync(IPage page, ILocator button, ILocator result)
    {
        for (var attempt = 0; attempt < 10 && !await result.IsVisibleAsync(); attempt++)
        {
            if (await button.IsVisibleAsync() && await button.IsEnabledAsync())
            {
                await button.ClickAsync();
            }
            await page.WaitForTimeoutAsync(500);
        }
        await Assertions.Expect(result).ToBeVisibleAsync();
    }

    [Fact]
    public async Task A_newcomer_waits_with_no_menu_then_the_Admin_grants_two_roles_from_Registrations()
    {
        using var factory = new UiWebFactory();
        factory.StartServer();
        var baseUrl = factory.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First().TrimEnd('/');
        var personas = await CaptureSeed.SeedAsync(factory);
        var newcomer = await AddNewcomerAsync(factory);

        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });

        // The newcomer: the in-progress message, and Home is the only menu item.
        var waiting = await SignInAsync(browser, baseUrl, newcomer);
        await waiting.GotoAsync(baseUrl + "/", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
        await Assertions.Expect(waiting.Locator("#registration-status"))
            .ToHaveTextAsync("Your registration is in progress. An Admin of Shrewsbury Orthodox Church will give you access.");
        await Assertions.Expect(waiting.Locator("nav[aria-label='Main'] a")).ToHaveCountAsync(1);
        await AssertNoWcagViolationsAsync(waiting, "the home page of someone with no role");
        await AssertNoSidewaysScrollAsync(waiting, "The home page of someone with no role");

        // The Admin: the menu counts what is waiting and opens Registrations.
        var admin = await SignInAsync(browser, baseUrl, personas["admin"]);
        await admin.GotoAsync(baseUrl + "/", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
        var registrations = admin.Locator("nav[aria-label='Main'] a[href='/registration/approve']");
        await Assertions.Expect(registrations).ToContainTextAsync("Registrations");
        await Assertions.Expect(registrations.Locator(".nav-count")).ToHaveCountAsync(1);
        await admin.GotoAsync(baseUrl + "/registration/approve", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
        await Assertions.Expect(admin.GetByRole(AriaRole.Heading, new() { Name = "Waiting for a role" })).ToBeVisibleAsync();
        var row = admin.Locator("tr", new() { HasText = NewcomerName });
        await Assertions.Expect(row).ToContainTextAsync("Self-registration");
        await Assertions.Expect(row).ToContainTextAsync("None");
        await AssertNoWcagViolationsAsync(admin, "Registrations");
        await AssertNoSidewaysScrollAsync(admin, "Registrations");

        // Two roles at once, confirmed by name.
        var grant = row.GetByRole(AriaRole.Button, new() { Name = $"Grant the chosen roles to {NewcomerName}" });
        await ClickUntilAsync(admin, admin.Locator($"#grant-{NewcomerId}-Teacher"), admin.Locator($"#grant-{NewcomerId}-Teacher:checked"));
        await admin.Locator($"#grant-{NewcomerId}-Parent").CheckAsync();
        await Assertions.Expect(grant).ToBeEnabledAsync();
        await grant.ClickAsync();
        await Assertions.Expect(admin.GetByText($"Grant Teacher and Parent to {NewcomerName}?")).ToBeVisibleAsync();
        await AssertNoWcagViolationsAsync(admin, "the grant confirmation");
        await admin.GetByRole(AriaRole.Button, new() { Name = $"Grant to {NewcomerName}" }).ClickAsync();

        await Assertions.Expect(admin.GetByRole(AriaRole.Status).Filter(new() { HasText = "granted" }))
            .ToHaveTextAsync($"Teacher and Parent granted to {NewcomerName}.");
        await Assertions.Expect(admin.Locator("tr", new() { HasText = NewcomerName })).ToHaveCountAsync(0);
    }
}
