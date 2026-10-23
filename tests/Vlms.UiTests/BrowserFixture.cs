using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;

namespace Vlms.UiTests;

/// <summary>
/// One app on a real port and one Chromium for the whole run, shared by every test class through
/// <see cref="BrowserCollection"/>. Each test opens its own browser context (isolated cookies, viewport).
/// </summary>
public sealed class BrowserFixture : IAsyncLifetime
{
    private UiWebFactory? _factory;
    private IPlaywright? _playwright;

    public IBrowser Browser { get; private set; } = null!;
    public string BaseUrl { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        _factory = new UiWebFactory();
        _factory.StartServer();
        // With UseKestrel(0) the bound port is only known once the server is up: read it from the server's address feature.
        var addresses = _factory.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses;
        BaseUrl = addresses.First().TrimEnd('/');

        _playwright = await Playwright.CreateAsync();
        try
        {
            Browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        }
        catch (PlaywrightException ex) when (ex.Message.Contains("Executable doesn't exist", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The Playwright Chromium browser is not installed on this machine. Build Vlms.UiTests, then run: "
                + "pwsh tests/Vlms.UiTests/bin/Debug/net10.0/playwright.ps1 install chromium", ex);
        }
    }

    public async Task DisposeAsync()
    {
        if (Browser is not null) await Browser.CloseAsync();
        _playwright?.Dispose();
        _factory?.Dispose();
    }

    /// <summary>A fresh page in its own context at the given viewport.</summary>
    public async Task<IPage> NewPageAsync(int width = 1280, int height = 800)
    {
        var context = await Browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = width, Height = height },
            Locale = "en-GB",
        });
        return await context.NewPageAsync();
    }
}

[CollectionDefinition(Name)]
public sealed class BrowserCollection : ICollectionFixture<BrowserFixture>
{
    public const string Name = "browser";
}
