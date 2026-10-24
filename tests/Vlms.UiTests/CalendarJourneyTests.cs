using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using Vlms.UiTests.Capture;

namespace Vlms.UiTests;

/// <summary>
/// The calendar (functional.md FR-022), driven in Chromium against the real app with
/// <see cref="CaptureSeed"/>'s data: a parent opens a day's saints and readings from the list.
/// </summary>
public sealed class CalendarJourneyTests
{
    [Theory]
    [InlineData("parent")]
    [InlineData("teacher")]
    [InlineData("admin-teacher")]
    public async Task Opens_todays_saints_and_readings(string persona)
    {
        using var factory = new UiWebFactory();
        factory.StartServer();
        var baseUrl = factory.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First().TrimEnd('/');
        var personas = await CaptureSeed.SeedAsync(factory);

        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        await using var context = await browser.NewContextAsync(new BrowserNewContextOptions { Locale = "en-GB" });
        var page = await context.NewPageAsync();
        await DesignCapture.SignInAsync(page, baseUrl, personas[persona]);

        await page.GotoAsync(baseUrl + "/calendar", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
        var toggle = page.Locator(".day--today .day__toggle");
        await toggle.ClickAsync();

        await Assertions.Expect(toggle).ToHaveAttributeAsync("aria-expanded", "true");
        var detail = page.Locator(".day--today .day__detail");
        await Assertions.Expect(detail).ToContainTextAsync("Tabitha the Merciful");
        await Assertions.Expect(detail).ToContainTextAsync("Gospel Reading: Luke 8:26-39");
    }
}
