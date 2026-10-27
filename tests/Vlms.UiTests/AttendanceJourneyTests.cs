using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using Vlms.UiTests.Capture;

namespace Vlms.UiTests;

/// <summary>
/// The teacher's session journey with the register (functional.md FR-020, FR-022, TC-052), driven in
/// Chromium against the real app: from the calendar, open today's meeting day, see their own class first
/// and the day's total, and update their class's register with one child marked absent; on Record completions the
/// "here and ready" shortcut leaves the absent child out; recording for them needs the catch-up
/// confirmation, and then it is recorded. Its own app instance with <see cref="CaptureSeed"/>'s data,
/// so the shared public-page fixture stays unseeded.
/// </summary>
public sealed class AttendanceJourneyTests
{
    [Fact]
    public async Task Teacher_takes_the_register_then_records_a_catch_up_for_an_absent_child_only_after_confirming()
    {
        using var factory = new UiWebFactory();
        factory.StartServer();
        var baseUrl = factory.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First().TrimEnd('/');
        var personas = await CaptureSeed.SeedAsync(factory);

        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        await using var context = await browser.NewContextAsync(new BrowserNewContextOptions { Locale = "en-GB" });
        var page = await context.NewPageAsync();
        await DesignCapture.SignInAsync(page, baseUrl, personas["teacher"]);

        // From the calendar (the grid, on a wide screen), open today's meeting day: the Shield-bearer
        // class (the teacher's own) is first.
        await page.GotoAsync(baseUrl + "/calendar", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
        await page.Locator(".cell--today .cell__button").ClickAsync();
        await page.Locator("#selected-day .day__event a").ClickAsync();
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await Assertions.Expect(page.Locator(".class-row").First).ToContainTextAsync("Your class");
        await Assertions.Expect(page.Locator(".event__total")).ToContainTextAsync("Day total: 17 present, 1 absent");

        // Update the Shield-bearer register: Alexander Hughes (consent in force) is not here.
        await page.GetByRole(AriaRole.Link, new() { Name = "Update the Shield-bearer register" }).ClickAsync();
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await page.GetByRole(AriaRole.Button, new() { Name = "Mark everyone present" }).ClickAsync();
        await page.GetByLabel("Alexander Hughes").UncheckAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Save changes to the register" }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Status).Filter(new() { HasText = "Register saved" })).ToContainTextAsync("5 present, 1 absent");

        // Record completions for this meeting: Shield-bearer, lesson 6 (St Paul).
        await page.GetByRole(AriaRole.Link, new() { Name = "Record the lessons completed at this meeting" }).ClickAsync();
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        // A tick made before the page's live connection is ready can be lost when it starts, so tick again until the button appears.
        var selectEveryone = page.GetByRole(AriaRole.Button, new() { Name = "Select everyone here and ready at Shield-bearer" });
        for (var attempt = 0; attempt < 10 && !await selectEveryone.IsVisibleAsync(); attempt++)
        {
            await page.GetByLabel("Shield-bearer", new() { Exact = true }).CheckAsync();
            await page.GetByLabel("St Paul").CheckAsync();
            await page.WaitForTimeoutAsync(500);
        }

        await selectEveryone.ClickAsync();

        var alex = page.GetByLabel("Alexander Hughes");
        await Assertions.Expect(alex).Not.ToBeCheckedAsync();
        await Assertions.Expect(page.GetByLabel("Andrew Shaw")).ToBeCheckedAsync();

        await alex.CheckAsync();
        var record = page.Locator(".record__bar button");
        await Assertions.Expect(page.GetByText("is marked absent on the register")).ToBeVisibleAsync();
        await Assertions.Expect(record).ToBeDisabledAsync();

        await page.GetByLabel("as a catch-up").CheckAsync();
        await Assertions.Expect(record).ToBeEnabledAsync();
        await record.ClickAsync();
        // Recording does several database round trips before the result appears, which can take longer than the default under load.
        await Assertions.Expect(page.GetByRole(AriaRole.Status).Filter(new() { HasText = "recorded" })).ToContainTextAsync("Alexander Hughes: recorded", new() { Timeout = 15000 });
    }
}
