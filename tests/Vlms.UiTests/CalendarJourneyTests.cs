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

    [Fact]
    public async Task An_admin_adds_a_weekly_service_and_an_excursion_whose_one_register_a_teacher_takes()
    {
        using var factory = new UiWebFactory();
        factory.StartServer();
        var baseUrl = factory.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First().TrimEnd('/');
        var personas = await CaptureSeed.SeedAsync(factory);

        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        await using var context = await browser.NewContextAsync(new BrowserNewContextOptions { Locale = "en-GB" });
        var page = await context.NewPageAsync();
        await DesignCapture.SignInAsync(page, baseUrl, personas["admin-teacher"]);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        // A weekly service: four dates, no register.
        await page.GotoAsync(baseUrl + "/calendar/events/new", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
        await page.GetByLabel("Service").CheckAsync();
        await page.GetByLabel("Date").FillAsync(today.AddDays(1).ToString("yyyy-MM-dd"));
        await page.GetByLabel("Name").FillAsync("Divine Liturgy");
        await page.GetByLabel("Repeats").SelectOptionAsync("Weekly");
        await page.GetByLabel("Last date").FillAsync(today.AddDays(22).ToString("yyyy-MM-dd"));
        await page.GetByRole(AriaRole.Button, new() { Name = "Add the event" }).ClickAsync();
        await page.WaitForURLAsync(new System.Text.RegularExpressions.Regex(@"/calendar/events/\d+$"));
        await Assertions.Expect(page.Locator("h1")).ToHaveTextAsync("Divine Liturgy");
        await Assertions.Expect(page.Locator("#registers-heading")).ToHaveCountAsync(0);
        await page.GotoAsync(baseUrl + "/attendance", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
        await Assertions.Expect(page.Locator("main")).Not.ToContainTextAsync("Divine Liturgy");

        // An excursion three days ago: one register for everyone.
        await page.GotoAsync(baseUrl + "/calendar/events/new", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
        await page.GetByLabel("Excursion").CheckAsync();
        await page.GetByLabel("Date").FillAsync(today.AddDays(-3).ToString("yyyy-MM-dd"));
        await page.GetByLabel("Name").FillAsync("Trip to Lichfield");
        await page.GetByRole(AriaRole.Button, new() { Name = "Add the event" }).ClickAsync();
        await page.WaitForURLAsync(new System.Text.RegularExpressions.Regex(@"/calendar/events/\d+$"));
        await Assertions.Expect(page.Locator(".class-row")).ToHaveCountAsync(1);
        await page.GetByRole(AriaRole.Link, new() { Name = "Take the excursion register" }).ClickAsync();
        await Assertions.Expect(page.Locator("h1")).ToHaveTextAsync("Excursion register");
        await page.GetByRole(AriaRole.Button, new() { Name = "Mark everyone present" }).ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Save the register" }).ClickAsync();
        await Assertions.Expect(page.Locator(".register__saved")).ToContainTextAsync("Register saved");
        await Assertions.Expect(page.Locator(".register__saved")).Not.ToContainTextAsync("Record the lessons");
    }

    [Fact]
    public async Task A_national_administrator_adds_and_imports_saints_that_show_in_a_chapters_calendar()
    {
        using var factory = new UiWebFactory();
        factory.StartServer();
        var baseUrl = factory.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First().TrimEnd('/');
        var personas = await CaptureSeed.SeedAsync(factory);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        await using var national = await browser.NewContextAsync(new BrowserNewContextOptions { Locale = "en-GB" });
        var page = await national.NewPageAsync();
        await DesignCapture.SignInAsync(page, baseUrl, personas["national"]);

        await page.GotoAsync(baseUrl + "/administration/saints", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
        await page.GetByLabel("Day").SelectOptionAsync(today.Day.ToString());
        await page.GetByLabel("Month").SelectOptionAsync(today.Month.ToString());
        await page.GetByLabel("Name", new() { Exact = true }).FillAsync("Winefride");
        await page.GetByRole(AriaRole.Button, new() { Name = "Add the saint" }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Status)).ToContainTextAsync("Winefride added");

        // An import: one new saint, one the Church calendar already lists that day (left unticked), one error.
        var dayText = today.ToString("dd/MM");
        var csv = string.Join("\r\n", "date,name", $"{dayText},Beuno", $"{dayText},Tabitha the Merciful", "31/04,Nobody", "");
        await page.GetByLabel("A .csv file").SetInputFilesAsync(new FilePayload { Name = "saints.csv", MimeType = "text/csv", Buffer = System.Text.Encoding.UTF8.GetBytes(csv) });
        await page.GetByRole(AriaRole.Button, new() { Name = "Preview the file" }).ClickAsync();
        await Assertions.Expect(page.Locator("main")).ToContainTextAsync("Already lists Tabitha the Merciful.");
        await Assertions.Expect(page.Locator("main")).ToContainTextAsync("Can't add:");
        await Assertions.Expect(page.GetByLabel("Add Tabitha the Merciful")).Not.ToBeCheckedAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Add 1 saint" }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Status)).ToContainTextAsync("1 saint added.");
        await Assertions.Expect(page.Locator("#list-heading")).ToHaveTextAsync("Saints added (2)");

        await using var parent = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            Locale = "en-GB", ViewportSize = new ViewportSize { Width = 375, Height = 812 }
        });
        var parentPage = await parent.NewPageAsync();
        await DesignCapture.SignInAsync(parentPage, baseUrl, personas["parent"]);
        await parentPage.GotoAsync(baseUrl + "/calendar", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
        await parentPage.Locator(".day--today .day__toggle").ClickAsync();
        var saints = parentPage.Locator(".day--today .day__saints li");
        await Assertions.Expect(saints).ToHaveTextAsync(["Martyrs Marcian and Martyrius", "Tabitha the Merciful", "Beuno", "Winefride"]);
    }
}
