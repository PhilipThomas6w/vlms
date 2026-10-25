using Deque.AxeCore.Commons;
using Deque.AxeCore.Playwright;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using SkiaSharp;
using Vlms.Domain;
using Vlms.Infrastructure;
using Vlms.UiTests.Capture;

namespace Vlms.UiTests;

/// <summary>
/// App-hosted lessons (docs/adr/0012-app-hosted-lessons.md) in Chromium against the real app: the lesson page with its
/// picture, the Teacher's Information shown to a teacher and not to a parent, axe-core (WCAG 2.2 A/AA) with no
/// violations, no horizontal scrolling at 375 px, the print layout, and saving the lesson to the device and reading it
/// with no connection.
/// </summary>
public sealed class LessonPageJourneyTests
{
    private static readonly string[] WcagTags = ["wcag2a", "wcag2aa", "wcag21aa", "wcag22aa"];
    private const string Code = "R5-01";

    private static async Task<(UiWebFactory Factory, string BaseUrl, IReadOnlyDictionary<string, CapturePersona> Personas, int PictureId)> StartAsync()
    {
        var factory = new UiWebFactory();
        factory.StartServer();
        var baseUrl = factory.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First().TrimEnd('/');
        var personas = await CaptureSeed.SeedAsync(factory);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<VlmsDbContext>();
        var lesson = await db.Lessons.SingleAsync(l => l.Code == Code);
        db.Entry(lesson).Property(l => l.Body).CurrentValue =
            "## Questions\n\n- What is a Prosphora?\n\n![Three loaves of prosphora](loaves.jpg)\n\n| Ingredient | Amount |\n|---|---|\n| Strong White Flour | 12 oz |\n";
        db.Entry(lesson).Property(l => l.ContentVersion).CurrentValue = "ui-test-1";
        var pictureId = await db.LessonPictures.Select(p => (int?)p.Id).MaxAsync() is { } max ? max + 1 : 1;
        db.LessonPictures.Add(new LessonPicture { Id = pictureId, LessonId = lesson.Id, Order = 1, FileName = "loaves.jpg", BlobKey = $"lesson-pictures/{Code}/loaves.jpg" });
        var rank = await db.Ranks.SingleAsync(r => r.Id == lesson.RankId);
        db.Entry(rank).Property(r => r.TeachersInformation).CurrentValue = "## Before the term\n\nBake a loaf with the class in the first week.";
        await db.SaveChangesAsync();
        await scope.ServiceProvider.GetRequiredService<IBlobStorage>().UploadAsync($"lesson-pictures/{Code}/loaves.jpg", Jpeg());

        return (factory, baseUrl, personas, pictureId);
    }

    private static byte[] Jpeg()
    {
        using var bitmap = new SKBitmap(1200, 800);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Wheat);
        }
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 80);
        return data.ToArray();
    }

    private static async Task AssertNoWcagViolationsAsync(IPage page)
    {
        var result = await page.RunAxe(new AxeRunOptions { RunOnly = new RunOnlyOptions { Type = "tag", Values = [.. WcagTags] } });
        Assert.True(result.Violations.Length == 0,
            "WCAG violations on the lesson page: " + string.Join("; ", result.Violations.Select(v => $"{v.Id} ({string.Join(", ", v.Nodes.Take(3).Select(n => n.Target))})")));
    }

    [Fact]
    public async Task Teacher_reads_the_lesson_with_the_teachers_information_on_a_phone_and_in_print()
    {
        var (factory, baseUrl, personas, _) = await StartAsync();
        using var _ = factory;
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        await using var context = await browser.NewContextAsync(new BrowserNewContextOptions { Locale = "en-GB" });
        var page = await context.NewPageAsync();
        await DesignCapture.SignInAsync(page, baseUrl, personas["teacher"]);

        // From the library, the title links to the page.
        await page.GotoAsync(baseUrl + "/lessons", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
        await page.Locator($"#lesson-{Code} a[href='/lessons/{Code}']").ClickAsync();
        await page.WaitForURLAsync($"**/lessons/{Code}");
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Level = 1, Name = "Prosphora" })).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Level = 2, Name = "Questions" })).ToBeVisibleAsync();
        var picture = page.GetByRole(AriaRole.Img, new() { Name = "Three loaves of prosphora" });
        await picture.ScrollIntoViewIfNeededAsync();
        await Assertions.Expect(picture).ToBeVisibleAsync();
        await page.WaitForFunctionAsync("() => { const i = document.querySelector('.lesson-text img'); return i && i.complete && i.naturalWidth > 0; }");

        var information = page.Locator("[data-teachers-information]");
        await Assertions.Expect(information).ToHaveCountAsync(1);
        await information.Locator("summary").ClickAsync();
        await Assertions.Expect(information).ToContainTextAsync("Bake a loaf with the class");
        await AssertNoWcagViolationsAsync(page);

        await page.SetViewportSizeAsync(375, 800);
        Assert.False(await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth > window.innerWidth"),
            "The lesson page scrolls horizontally at 375 px.");
        await AssertNoWcagViolationsAsync(page);

        await page.EmulateMediaAsync(new PageEmulateMediaOptions { Media = Media.Print });
        await Assertions.Expect(page.Locator(".shell-bar")).ToBeHiddenAsync();
        await Assertions.Expect(page.Locator(".lesson-back")).ToBeHiddenAsync();
        await Assertions.Expect(page.Locator("article.lesson-text")).ToBeVisibleAsync();
    }

    [Fact]
    public async Task Parent_reads_the_lesson_without_the_teachers_information()
    {
        var (factory, baseUrl, personas, _) = await StartAsync();
        using var _ = factory;
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        await using var context = await browser.NewContextAsync(new BrowserNewContextOptions { Locale = "en-GB" });
        var page = await context.NewPageAsync();
        await DesignCapture.SignInAsync(page, baseUrl, personas["parent"]);

        await page.GotoAsync(baseUrl + $"/lessons/{Code}", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });

        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Level = 1, Name = "Prosphora" })).ToBeVisibleAsync();
        await Assertions.Expect(page.Locator("[data-teachers-information]")).ToHaveCountAsync(0);
        await Assertions.Expect(page.Locator("main")).Not.ToContainTextAsync("Teacher's Information");
        await Assertions.Expect(page.Locator("main")).Not.ToContainTextAsync("Bake a loaf");
        await AssertNoWcagViolationsAsync(page);
    }

    [Fact]
    public async Task Saves_a_converted_lesson_and_reads_it_with_no_connection()
    {
        var (factory, baseUrl, personas, pictureId) = await StartAsync();
        using var _ = factory;
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        await using var context = await browser.NewContextAsync(new BrowserNewContextOptions { Locale = "en-GB" });
        var page = await context.NewPageAsync();
        await DesignCapture.SignInAsync(page, baseUrl, personas["teacher"]);

        await page.GotoAsync(baseUrl + "/lessons", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
        await page.EvaluateAsync("() => navigator.serviceWorker.ready");
        await page.WaitForFunctionAsync("() => navigator.serviceWorker.controller !== null");

        var block = page.Locator($"#lesson-{Code} [data-offline-lesson]");
        await block.GetByRole(AriaRole.Button, new() { Name = "Save to this device" }).ClickAsync();
        await Assertions.Expect(block.GetByRole(AriaRole.Status)).ToHaveTextAsync("Saved on this device");

        await context.SetOfflineAsync(true);
        await page.GotoAsync(baseUrl + "/calendar");
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "No internet connection" })).ToBeVisibleAsync();
        await page.GetByRole(AriaRole.Link, new() { Name = "Prosphora" }).ClickAsync();
        await page.WaitForURLAsync($"**/lessons/{Code}/offline");

        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Level = 1, Name = "Prosphora" })).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Img, new() { Name = "Three loaves of prosphora" })).ToBeVisibleAsync();
        await page.WaitForFunctionAsync("() => { const i = document.querySelector('img'); return i && i.complete && i.naturalWidth > 0; }");
        Assert.Contains($"/lessons/pictures/{pictureId}", await page.GetByRole(AriaRole.Img).GetAttributeAsync("src"));
        await AssertNoWcagViolationsAsync(page);
    }
}
