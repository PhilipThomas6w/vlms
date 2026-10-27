using Deque.AxeCore.Commons;
using Deque.AxeCore.Playwright;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using SkiaSharp;
using Vlms.Domain;
using Vlms.Infrastructure;
using Vlms.Infrastructure.Identity;
using Vlms.UiTests.Capture;

namespace Vlms.UiTests;

/// <summary>
/// Lesson suggestions, review and history (FR-007, docs/adr/0013) in Chromium against the real app: a Teacher suggests a
/// change in the editor and submits it, an Approver approves it and reads the history; an Approver publishes at once
/// ("Publish now"); an Approver replaces the whole lesson; an Approver sends a suggestion back and its author sees why; an
/// Approver refers a suggestion to committee and three Approvers approve it, with the menu count along the way.
/// Every page passes axe-core (WCAG 2.2 A/AA).
/// </summary>
public sealed class LessonSuggestionJourneyTests
{
    private static readonly string[] WcagTags = ["wcag2a", "wcag2aa", "wcag21aa", "wcag22aa"];
    private const string Code = "R5-01";

    private static async Task<(UiWebFactory Factory, string BaseUrl, IReadOnlyDictionary<string, CapturePersona> Personas)> StartAsync()
    {
        var factory = new UiWebFactory();
        factory.StartServer();
        var baseUrl = factory.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First().TrimEnd('/');
        var personas = await CaptureSeed.SeedAsync(factory);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<VlmsDbContext>();
        var lesson = await db.Lessons.SingleAsync(l => l.Code == Code);
        db.Entry(lesson).Property(l => l.Body).CurrentValue = "## Questions\n\n- What is a Prosphora?\n- Who bakes it?\n";
        db.Entry(lesson).Property(l => l.ContentVersion).CurrentValue = "ui-test-1";
        await db.SaveChangesAsync();
        return (factory, baseUrl, personas);
    }

    private static byte[] Png()
    {
        using var bitmap = new SKBitmap(400, 300);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Wheat);
        }
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static async Task AssertNoWcagViolationsAsync(IPage page, string where)
    {
        var result = await page.RunAxe(new AxeRunOptions { RunOnly = new RunOnlyOptions { Type = "tag", Values = [.. WcagTags] } });
        Assert.True(result.Violations.Length == 0,
            $"WCAG violations on {where}: " + string.Join("; ", result.Violations.Select(v => $"{v.Id} ({string.Join(", ", v.Nodes.Take(3).Select(n => n.Target))})")));
    }

    private static async Task<IPage> SignInAsync(IBrowser browser, string baseUrl, CapturePersona persona)
    {
        var context = await browser.NewContextAsync(new BrowserNewContextOptions { Locale = "en-GB" });
        var page = await context.NewPageAsync();
        await DesignCapture.SignInAsync(page, baseUrl, persona);
        return page;
    }

    private static ILocator Editable(IPage page) => page.Locator(".lesson-editor [role='textbox'][aria-label='Lesson text']");

    // The editor is made once the page's live connection is ready, so it being there means buttons will respond.
    private static async Task OpenEditorAsync(IPage page, string baseUrl)
    {
        await page.GotoAsync(baseUrl + $"/lessons/{Code}", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
        await page.GetByRole(AriaRole.Link, new() { Name = "Suggest changes" }).ClickAsync();
        await page.WaitForURLAsync($"**/lessons/{Code}/suggest");
        await Assertions.Expect(Editable(page)).ToBeVisibleAsync(new() { Timeout = 15000 });
    }

    private static async Task AddQuestionAsync(IPage page, string text)
    {
        var editable = Editable(page);
        await editable.ClickAsync();
        await page.Keyboard.PressAsync("Control+End");
        await page.Keyboard.PressAsync("Enter");
        await page.Keyboard.TypeAsync(text);
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
    public async Task Teacher_suggests_and_submits_then_an_approver_approves_and_reads_the_history()
    {
        var (factory, baseUrl, personas) = await StartAsync();
        using var _ = factory;
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });

        var teacher = await SignInAsync(browser, baseUrl, personas["teacher"]);
        await OpenEditorAsync(teacher, baseUrl);
        await AssertNoWcagViolationsAsync(teacher, "the editor");
        await AddQuestionAsync(teacher, "Why is it stamped with a seal?");
        await teacher.GetByLabel("Reason for the change (required)").FillAsync("Add a question about the seal.");
        await ClickUntilAsync(teacher, teacher.GetByRole(AriaRole.Button, new() { Name = "Submit for review" }),
            teacher.GetByText("Submitted for review."));
        await AssertNoWcagViolationsAsync(teacher, "the editor after submitting");

        // Others now see the change in progress on the lesson page.
        var approver = await SignInAsync(browser, baseUrl, personas["approver"]);
        await approver.GotoAsync(baseUrl + $"/lessons/{Code}", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
        await Assertions.Expect(approver.GetByText("A change to this lesson is awaiting review.")).ToBeVisibleAsync();
        await AssertNoWcagViolationsAsync(approver, "the lesson page with a change in progress");

        await approver.GotoAsync(baseUrl + "/curriculum/review", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
        await AssertNoWcagViolationsAsync(approver, "Changes to review");
        await approver.GetByRole(AriaRole.Link, new() { Name = "Prosphora" }).ClickAsync();
        await approver.WaitForURLAsync("**/curriculum/review/*");
        await approver.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await Assertions.Expect(approver.Locator("article.lesson-diff ins")).ToContainTextAsync("seal");
        await AssertNoWcagViolationsAsync(approver, "the review page");

        await ClickUntilAsync(approver, approver.GetByRole(AriaRole.Button, new() { Name = "Approve" }),
            approver.GetByText("The lesson is now version 2."));
        await approver.GetByRole(AriaRole.Link, new() { Name = "See the lesson’s history" }).ClickAsync();
        await approver.WaitForURLAsync($"**/lessons/{Code}/history");
        await approver.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await Assertions.Expect(approver.GetByRole(AriaRole.Heading, new() { Name = "Version 2 (current)" })).ToBeVisibleAsync();
        await ClickUntilAsync(approver, approver.GetByRole(AriaRole.Button, new() { Name = "Show changes" }).First,
            approver.Locator(".lesson-diff ins").First);
        await AssertNoWcagViolationsAsync(approver, "the history page");

        // Everyone now reads version 2.
        await teacher.GotoAsync(baseUrl + $"/lessons/{Code}", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
        await Assertions.Expect(teacher.Locator("article.lesson-text")).ToContainTextAsync("Why is it stamped with a seal?");
        await Assertions.Expect(teacher.GetByText("Version 2")).ToBeVisibleAsync();
    }

    // Another Approver, for the committee (adr/0013 decision 8): the capture seed has one.
    private static async Task<CapturePersona> AddApproverAsync(UiWebFactory factory, int id, string display, string userName)
    {
        using var scope = factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<VlmsIdentityUser>>();
        var created = await users.CreateAsync(new VlmsIdentityUser { Id = id, UserName = userName, Email = userName, EmailConfirmed = true }, CaptureSeed.Password);
        Assert.True(created.Succeeded, string.Join("; ", created.Errors.Select(e => e.Description)));
        var db = scope.ServiceProvider.GetRequiredService<VlmsDbContext>();
        db.AppUsers.Add(new AppUser { Id = id, DisplayName = display, Email = userName });
        db.UserRoles.Add(new UserRole { UserId = id, Role = Role.Approver });
        await db.SaveChangesAsync();
        return new CapturePersona(userName, userName, null, id);
    }

    private static ILocator MenuCount(IPage page) =>
        page.GetByRole(AriaRole.Navigation, new() { Name = "Main" }).Locator("a[href='/curriculum/review'] .nav-count");

    [Fact]
    public async Task Approver_refers_a_suggestion_to_committee_and_the_third_approval_publishes_it()
    {
        var (factory, baseUrl, personas) = await StartAsync();
        using var _ = factory;
        var second = await AddApproverAsync(factory, 901, "Fr Gregory Approver", "approver2@capture.test");
        var third = await AddApproverAsync(factory, 902, "Anna Approver", "approver3@capture.test");
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });

        var teacher = await SignInAsync(browser, baseUrl, personas["teacher"]);
        await OpenEditorAsync(teacher, baseUrl);
        await AddQuestionAsync(teacher, "Why is it stamped with a seal?");
        await teacher.GetByLabel("Reason for the change (required)").FillAsync("Add a question about the seal.");
        await ClickUntilAsync(teacher, teacher.GetByRole(AriaRole.Button, new() { Name = "Submit for review" }),
            teacher.GetByText("Submitted for review."));

        // The menu shows the Approver one change awaiting them, in words as well as the number.
        var approver = await SignInAsync(browser, baseUrl, personas["approver"]);
        await approver.GotoAsync(baseUrl + "/", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
        await Assertions.Expect(MenuCount(approver)).ToHaveTextAsync("1");
        await Assertions.Expect(approver.GetByRole(AriaRole.Link, new() { Name = "Changes to review (1 awaiting review)" })).ToBeVisibleAsync();
        await AssertNoWcagViolationsAsync(approver, "the home page with a change awaiting review");

        await approver.GotoAsync(baseUrl + "/curriculum/review", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
        await approver.GetByRole(AriaRole.Link, new() { Name = "Prosphora" }).ClickAsync();
        await approver.WaitForURLAsync("**/curriculum/review/*");
        await approver.WaitForLoadStateAsync(LoadState.NetworkIdle);
        var reviewUrl = approver.Url;
        await ClickUntilAsync(approver, approver.GetByRole(AriaRole.Button, new() { Name = "Refer to committee" }),
            approver.GetByText("Referred to committee."));
        await AssertNoWcagViolationsAsync(approver, "the review page after referring");
        await ClickUntilAsync(approver, approver.GetByRole(AriaRole.Button, new() { Name = "Approve", Exact = true }),
            approver.GetByText("1 of 3 approvals."));
        await Assertions.Expect(approver.GetByText("You have approved this.")).ToBeVisibleAsync();

        // Having approved, nothing awaits this Approver; the list shows where the committee has got to.
        await approver.GotoAsync(baseUrl + "/curriculum/review", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
        await Assertions.Expect(MenuCount(approver)).ToHaveCountAsync(0);
        await Assertions.Expect(approver.GetByText("Referred to committee: 1 of 3 approved (you approved)")).ToBeVisibleAsync();
        await AssertNoWcagViolationsAsync(approver, "Changes to review with a referred change");

        var secondPage = await SignInAsync(browser, baseUrl, second);
        await secondPage.GotoAsync(reviewUrl, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
        await Assertions.Expect(MenuCount(secondPage)).ToHaveTextAsync("1");
        await ClickUntilAsync(secondPage, secondPage.GetByRole(AriaRole.Button, new() { Name = "Approve", Exact = true }),
            secondPage.GetByText("2 of 3 approvals."));

        var thirdPage = await SignInAsync(browser, baseUrl, third);
        await thirdPage.GotoAsync(reviewUrl, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
        await AssertNoWcagViolationsAsync(thirdPage, "the review page of a referred change");
        await ClickUntilAsync(thirdPage, thirdPage.GetByRole(AriaRole.Button, new() { Name = "Approve", Exact = true }),
            thirdPage.GetByText("The lesson is now version 2."));

        await thirdPage.GetByRole(AriaRole.Link, new() { Name = "See the lesson’s history" }).ClickAsync();
        await thirdPage.WaitForURLAsync($"**/lessons/{Code}/history");
        await thirdPage.WaitForLoadStateAsync(LoadState.NetworkIdle);
        var latest = thirdPage.Locator(".version-list > li").First;
        await Assertions.Expect(latest).ToContainTextAsync("Referred to committee");
        await Assertions.Expect(latest).ToContainTextAsync("Elizabeth Approver");
        await Assertions.Expect(latest).ToContainTextAsync("Fr Gregory Approver");
        await Assertions.Expect(latest).ToContainTextAsync("Anna Approver");
        await AssertNoWcagViolationsAsync(thirdPage, "the history of a change approved by committee");

        await teacher.GotoAsync(baseUrl + $"/lessons/{Code}", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
        await Assertions.Expect(teacher.Locator("article.lesson-text")).ToContainTextAsync("Why is it stamped with a seal?");
    }

    [Fact]
    public async Task Approver_publishes_a_change_at_once()
    {
        var (factory, baseUrl, personas) = await StartAsync();
        using var _ = factory;
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });

        var approver = await SignInAsync(browser, baseUrl, personas["approver"]);
        await OpenEditorAsync(approver, baseUrl);
        await AddQuestionAsync(approver, "When is it offered?");

        // A picture, uploaded with its description, then shown in the editor.
        await approver.Keyboard.PressAsync("Enter");
        await approver.GetByRole(AriaRole.Button, new() { Name = "Insert picture" }).ClickAsync();
        var dialog = approver.GetByRole(AriaRole.Dialog, new() { Name = "Insert picture" });
        await Assertions.Expect(dialog).ToBeVisibleAsync();
        await AssertNoWcagViolationsAsync(approver, "the picture dialog");
        await dialog.GetByLabel("Picture (JPEG or PNG)").SetInputFilesAsync(new FilePayload { Name = "Seal.png", MimeType = "image/png", Buffer = Png() });
        await dialog.GetByLabel("Description (required)").FillAsync("The seal stamped on a prosphora");
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Insert picture" }).ClickAsync();
        await Assertions.Expect(dialog).ToBeHiddenAsync();
        await Assertions.Expect(Editable(approver).GetByRole(AriaRole.Img, new() { Name = "The seal stamped on a prosphora" })).ToBeVisibleAsync();

        await ClickUntilAsync(approver, approver.GetByRole(AriaRole.Button, new() { Name = "Publish now" }),
            approver.GetByText("The lesson is now version 2."));
        await AssertNoWcagViolationsAsync(approver, "the editor after publishing");

        await approver.GotoAsync(baseUrl + $"/lessons/{Code}", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
        await Assertions.Expect(approver.Locator("article.lesson-text")).ToContainTextAsync("When is it offered?");
        await approver.WaitForFunctionAsync("() => { const i = document.querySelector('article.lesson-text img'); return i && i.complete && i.naturalWidth > 0; }");
        await Assertions.Expect(approver.GetByRole(AriaRole.Img, new() { Name = "The seal stamped on a prosphora" })).ToBeVisibleAsync();
    }

    [Fact]
    public async Task Approver_replaces_the_whole_lesson_and_the_history_shows_both_side_by_side()
    {
        var (factory, baseUrl, personas) = await StartAsync();
        using var _ = factory;
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });

        var approver = await SignInAsync(browser, baseUrl, personas["approver"]);
        await OpenEditorAsync(approver, baseUrl);
        await ClickUntilAsync(approver, approver.GetByRole(AriaRole.Button, new() { Name = "Replace the whole lesson" }),
            approver.GetByText("The whole lesson will be replaced."));
        await Editable(approver).ClickAsync();
        await approver.Keyboard.TypeAsync("The lesson rewritten from the start.");
        await AssertNoWcagViolationsAsync(approver, "the editor replacing the whole lesson");
        await ClickUntilAsync(approver, approver.GetByRole(AriaRole.Button, new() { Name = "Publish now" }),
            approver.GetByText("The lesson is now version 2."));

        await approver.GotoAsync(baseUrl + $"/lessons/{Code}/history", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
        await ClickUntilAsync(approver, approver.GetByRole(AriaRole.Button, new() { Name = "Show changes" }).First,
            approver.Locator(".lesson-compare").First);
        var compare = approver.Locator(".lesson-compare").First;
        await Assertions.Expect(compare).ToContainTextAsync("What is a Prosphora?");
        await Assertions.Expect(compare).ToContainTextAsync("The lesson rewritten from the start.");
        await AssertNoWcagViolationsAsync(approver, "the history of a lesson replaced in full");
    }

    [Fact]
    public async Task Approver_sends_a_suggestion_back_and_its_author_sees_the_comment()
    {
        var (factory, baseUrl, personas) = await StartAsync();
        using var _ = factory;
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });

        var teacher = await SignInAsync(browser, baseUrl, personas["teacher"]);
        await OpenEditorAsync(teacher, baseUrl);
        await AddQuestionAsync(teacher, "Who eats the antidoron?");
        await teacher.GetByLabel("Reason for the change (required)").FillAsync("Add a question about the antidoron.");
        await ClickUntilAsync(teacher, teacher.GetByRole(AriaRole.Button, new() { Name = "Submit for review" }),
            teacher.GetByText("Submitted for review."));

        var approver = await SignInAsync(browser, baseUrl, personas["approver"]);
        await approver.GotoAsync(baseUrl + "/curriculum/review", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
        await approver.GetByRole(AriaRole.Link, new() { Name = "Prosphora" }).ClickAsync();
        await approver.WaitForURLAsync("**/curriculum/review/*");
        await approver.WaitForLoadStateAsync(LoadState.NetworkIdle);
        // Text typed before the page's live connection is ready can be lost too, so fill the comment on each attempt.
        var comment = approver.GetByLabel("Comment (required to send back");
        var sentBack = approver.GetByText("Sent back to Fr Daniel Teacher with your comment.");
        for (var attempt = 0; attempt < 10 && !await sentBack.IsVisibleAsync(); attempt++)
        {
            await comment.FillAsync("Explain what the antidoron is first.");
            await comment.BlurAsync();
            await approver.GetByRole(AriaRole.Button, new() { Name = "Send back" }).ClickAsync();
            await approver.WaitForTimeoutAsync(500);
        }
        await Assertions.Expect(sentBack).ToBeVisibleAsync();
        await AssertNoWcagViolationsAsync(approver, "the review page after sending back");

        await OpenEditorAsync(teacher, baseUrl);
        await Assertions.Expect(teacher.GetByText("Explain what the antidoron is first.")).ToBeVisibleAsync();
        await Assertions.Expect(Editable(teacher)).ToContainTextAsync("Who eats the antidoron?");
        await AssertNoWcagViolationsAsync(teacher, "the editor after a suggestion was sent back");
    }
}
