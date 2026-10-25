using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using Vlms.UiTests.Capture;

namespace Vlms.UiTests;

/// <summary>
/// The calendar (functional.md FR-022), driven in Chromium against the real app with
/// <see cref="CaptureSeed"/>'s data: a day's saints and readings open from the list (the default on
/// phones) and from the grid (the default on wider screens), and a person's chosen view is remembered.
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
        await using var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            Locale = "en-GB", ViewportSize = new ViewportSize { Width = 375, Height = 812 }
        });
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

    [Fact]
    public async Task Opens_a_day_from_the_grid_and_remembers_the_chosen_view()
    {
        using var factory = new UiWebFactory();
        factory.StartServer();
        var baseUrl = factory.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First().TrimEnd('/');
        var personas = await CaptureSeed.SeedAsync(factory);

        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        await using var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            Locale = "en-GB", ViewportSize = new ViewportSize { Width = 1280, Height = 900 }
        });
        var page = await context.NewPageAsync();
        await DesignCapture.SignInAsync(page, baseUrl, personas["parent"]);

        // Nothing chosen yet: the grid on a wide screen, the list hidden.
        await page.GotoAsync(baseUrl + "/calendar", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
        await Assertions.Expect(page.Locator(".month-grid")).ToBeVisibleAsync();
        await Assertions.Expect(page.Locator(".calendar__days")).ToBeHiddenAsync();
        await Assertions.Expect(page.Locator(".month-grid th").First).ToContainTextAsync("Monday");

        var today = page.Locator(".cell--today .cell__button");
        await today.ClickAsync();
        await Assertions.Expect(today).ToHaveAttributeAsync("aria-expanded", "true");
        var selected = page.Locator("#selected-day");
        await Assertions.Expect(selected).ToContainTextAsync("Tabitha the Merciful");
        await Assertions.Expect(selected).ToContainTextAsync("Gospel Reading: Luke 8:26-39");

        // Choosing the list is remembered: it stays after a reload, even on a wide screen.
        await page.Locator(".calendar__view--grid .view-switch button", new PageLocatorOptions { HasText = "List" }).ClickAsync();
        await Assertions.Expect(page.Locator(".calendar__days")).ToBeVisibleAsync();
        await page.ReloadAsync(new PageReloadOptions { WaitUntil = WaitUntilState.NetworkIdle });
        await Assertions.Expect(page.Locator(".calendar__days")).ToBeVisibleAsync();
        await Assertions.Expect(page.Locator(".month-grid")).ToHaveCountAsync(0);

        // Choosing the grid on a phone shows the grid there, without making the page scroll sideways.
        await page.Locator(".view-switch button", new PageLocatorOptions { HasText = "Grid" }).ClickAsync();
        await Assertions.Expect(page.Locator(".month-grid")).ToBeVisibleAsync();
        await page.SetViewportSizeAsync(375, 812);
        await page.ReloadAsync(new PageReloadOptions { WaitUntil = WaitUntilState.NetworkIdle });
        await Assertions.Expect(page.Locator(".month-grid")).ToBeVisibleAsync();
        var overflow = await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth > document.documentElement.clientWidth");
        Assert.False(overflow);
    }
}
