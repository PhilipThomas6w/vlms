using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using Vlms.Domain;
using Vlms.UiTests.Capture;

namespace Vlms.UiTests;

/// <summary>
/// Lessons saved for offline use (functional.md FR-023, adr/0011), driven in Chromium against the real app:
/// a teacher saves a lesson from the library, the connection drops, any page becomes the saved-lessons
/// page and the lesson's file still opens from the device; removing the lesson takes it all away again.
/// </summary>
public sealed class OfflineLessonsJourneyTests
{
    [Fact]
    public async Task Saves_a_lesson_opens_it_with_no_connection_and_removes_it()
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

        await page.GotoAsync(baseUrl + "/lessons", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
        var block = page.Locator("[data-offline-lesson]").First;
        await Assertions.Expect(block.GetByRole(AriaRole.Button, new() { Name = "Save to this device" })).ToBeVisibleAsync();
        var title = (await block.GetAttributeAsync("data-title"))!;

        // The seed's lesson files have no content; give this lesson's files some, under the keys the seed chose.
        var fileLinks = block.Locator("xpath=..").Locator("a[href^='/lessons/files/']");
        var fileIds = new List<int>();
        using (var scope = factory.Services.CreateScope())
        {
            var blobs = scope.ServiceProvider.GetRequiredService<IBlobStorage>();
            for (var i = 0; i < await fileLinks.CountAsync(); i++)
            {
                var id = int.Parse((await fileLinks.Nth(i).GetAttributeAsync("href"))!.Split('/').Last());
                var extension = Path.GetExtension((await fileLinks.Nth(i).InnerTextAsync()).Trim());
                await blobs.UploadAsync($"capture/missing-{id}{extension}", System.Text.Encoding.UTF8.GetBytes($"saved lesson file {id}"));
                fileIds.Add(id);
            }
        }

        // The service worker must be in control before the page is used, or its fetches are not intercepted.
        await page.EvaluateAsync("() => navigator.serviceWorker.ready");
        await page.WaitForFunctionAsync("() => navigator.serviceWorker.controller !== null");

        await block.GetByRole(AriaRole.Button, new() { Name = "Save to this device" }).ClickAsync();
        await Assertions.Expect(block.GetByRole(AriaRole.Status)).ToHaveTextAsync("Saved on this device");
        await Assertions.Expect(block.GetByRole(AriaRole.Button, new() { Name = "Remove" })).ToBeVisibleAsync();

        await context.SetOfflineAsync(true);
        await page.GotoAsync(baseUrl + "/calendar");
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "No internet connection" })).ToBeVisibleAsync();
        await Assertions.Expect(page.Locator("main")).ToContainTextAsync(title);

        var firstId = fileIds[0];
        var offlineBody = await page.EvaluateAsync<string>("async (id) => { const r = await fetch('/lessons/files/' + id); return r.status + ':' + await r.text(); }", firstId);
        Assert.Equal($"200:saved lesson file {firstId}", offlineBody);

        await context.SetOfflineAsync(false);
        await page.GotoAsync(baseUrl + "/lessons", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
        block = page.Locator("[data-offline-lesson]").First;
        await Assertions.Expect(block.GetByRole(AriaRole.Status)).ToHaveTextAsync("Saved on this device");
        await block.GetByRole(AriaRole.Button, new() { Name = "Remove" }).ClickAsync();
        await Assertions.Expect(block.GetByRole(AriaRole.Button, new() { Name = "Save to this device" })).ToBeVisibleAsync();

        await context.SetOfflineAsync(true);
        await page.GotoAsync(baseUrl + "/calendar");
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "No internet connection" })).ToBeVisibleAsync();
        await Assertions.Expect(page.Locator("main")).Not.ToContainTextAsync(title);
        var afterRemove = await page.EvaluateAsync<string>("async (id) => { try { const r = await fetch('/lessons/files/' + id); return String(r.status); } catch { return 'failed'; } }", firstId);
        Assert.Equal("failed", afterRemove);
    }
}
