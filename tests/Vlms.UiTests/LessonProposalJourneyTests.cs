using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using Vlms.UiTests.Capture;

namespace Vlms.UiTests;

/// <summary>
/// The Teacher's proposal page (/curriculum/teacher, FR-007) in Chromium, with the full curriculum seeded: the
/// proposal form opens below the list of every lesson, so "Propose edit" must bring it into view and give its
/// heading focus, or the click appears to do nothing.
/// </summary>
public sealed class LessonProposalJourneyTests
{
    [Fact]
    public async Task Propose_edit_brings_the_form_into_view_and_focuses_its_heading()
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
        await page.GotoAsync(baseUrl + "/curriculum/teacher", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });

        var proposeEdit = page.GetByRole(AriaRole.Button, new() { NameRegex = new System.Text.RegularExpressions.Regex("^Propose edit to ") }).First;
        var title = (await proposeEdit.GetAttributeAsync("aria-label"))!["Propose edit to ".Length..];
        var formHeading = page.GetByRole(AriaRole.Heading, new() { Name = $"Propose an edit to {title}", Exact = true });

        // A click made before the page's live connection is ready can be lost, so click again until the form opens.
        for (var attempt = 0; attempt < 10 && !await formHeading.IsVisibleAsync(); attempt++)
        {
            await proposeEdit.ClickAsync();
            await page.WaitForTimeoutAsync(500);
        }

        await Assertions.Expect(formHeading).ToBeFocusedAsync();
        await Assertions.Expect(formHeading).ToBeInViewportAsync();
    }
}
