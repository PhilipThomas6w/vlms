using System.IO;
using System.Text.Json;
using Xunit;

namespace Vlms.Tests.Web;

/// <summary>
/// Regression coverage for the PWA manifest/service worker (STATE.md "PWA manifest/service worker
/// for installability", adr/0001-technology-stack.md). There is no server-side logic here to unit
/// test in the usual sense — these are static assets plus a registration script — so this suite
/// checks the one thing that's cheap and meaningful to check at build time: the manifest is valid
/// JSON with the fields a browser needs, every icon it references actually exists on disk, the
/// service worker file exists and isn't empty, and the root HTML host (App.razor) actually wires
/// both of them up. It does not (and cannot, without a browser) verify runtime installability or
/// caching behaviour.
/// </summary>
public class PwaAssetsTests
{
    [Fact]
    public void Manifest_IsValidJsonWithRequiredFields()
    {
        var manifestPath = Path.Combine(WwwrootPath(), "manifest.json");
        Assert.True(File.Exists(manifestPath), $"Expected a manifest at {manifestPath}");

        using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var root = document.RootElement;

        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("name").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("short_name").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("start_url").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("display").GetString()));

        var icons = root.GetProperty("icons");
        Assert.True(icons.GetArrayLength() > 0, "manifest.json must declare at least one icon");
    }

    [Fact]
    public void Manifest_IconFiles_ExistOnDisk()
    {
        var wwwroot = WwwrootPath();
        var manifestPath = Path.Combine(wwwroot, "manifest.json");
        using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));

        foreach (var icon in document.RootElement.GetProperty("icons").EnumerateArray())
        {
            var src = icon.GetProperty("src").GetString();
            Assert.False(string.IsNullOrWhiteSpace(src));

            var iconPath = Path.Combine(wwwroot, src!.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(iconPath), $"manifest.json references missing icon: {src}");
        }
    }

    [Fact]
    public void ServiceWorker_FileExists_AndIsNotEmpty()
    {
        var serviceWorkerPath = Path.Combine(WwwrootPath(), "service-worker.js");
        Assert.True(File.Exists(serviceWorkerPath), $"Expected a service worker at {serviceWorkerPath}");
        Assert.False(string.IsNullOrWhiteSpace(File.ReadAllText(serviceWorkerPath)));
    }

    [Fact]
    public void AppRazor_LinksManifest_AndRegistersServiceWorker()
    {
        var appRazorPath = Path.Combine(RepoRoot(), "src", "Vlms.Web", "Components", "App.razor");
        Assert.True(File.Exists(appRazorPath), $"Expected the root HTML host at {appRazorPath}");

        var content = File.ReadAllText(appRazorPath);

        Assert.Contains("rel=\"manifest\"", content);
        Assert.Contains("manifest.json", content);
        Assert.Contains("navigator.serviceWorker.register", content);
        Assert.Contains("service-worker.js", content);
    }

    [Fact]
    public void ServiceWorker_StaticCacheNameChangesPerBuild_AndNeverDeletesSavedLessons()
    {
        var serviceWorker = File.ReadAllText(Path.Combine(WwwrootPath(), "service-worker.js"));
        var appRazor = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Vlms.Web", "Components", "App.razor"));

        // The cache name takes the build id from the script URL, which App.razor sets from the assembly's MVID.
        Assert.Contains("searchParams.get(\"v\")", serviceWorker);
        Assert.Contains("service-worker.js?v=@BuildId", appRazor);
        Assert.Contains("ModuleVersionId", appRazor);

        // Activate deletes old static caches only (FR-023: saved lessons live in "vlms-lessons").
        Assert.Contains("key.startsWith(STATIC_PREFIX)", serviceWorker);
        Assert.DoesNotContain("keys.filter((key) => key !== CACHE_NAME)", serviceWorker);
    }

    [Fact]
    public void ServiceWorker_CachesOnlyNonEmptyResponsesOfTheExpectedType()
    {
        var serviceWorker = File.ReadAllText(Path.Combine(WwwrootPath(), "service-worker.js"));

        // Both the precache and the runtime static cache go through the check; no unchecked writes remain.
        Assert.Contains("async function worthCaching(response, expectedType)", serviceWorker);
        Assert.Contains("body.byteLength > 0", serviceWorker);
        Assert.Contains("contentType.includes(expectedType)", serviceWorker);
        Assert.Contains("worthCaching(response, expectedTypeFor(path))", serviceWorker);
        Assert.Contains("worthCaching(response, EXPECTED_TYPES[request.destination])", serviceWorker);
        Assert.DoesNotContain("cache.addAll(", serviceWorker);
        Assert.DoesNotContain("if (response.ok) {", serviceWorker);
    }

    [Fact]
    public void OfflinePages_ExistAndTheLibraryLoadsTheSaveScript()
    {
        var wwwroot = WwwrootPath();
        foreach (var file in new[] { "offline.html", "offline.js", "offline-lessons.js", "add-to-home-screen.html" })
        {
            Assert.True(File.Exists(Path.Combine(wwwroot, file)), $"Expected {file} in wwwroot");
        }

        Assert.Contains("No internet connection", File.ReadAllText(Path.Combine(wwwroot, "offline.html")));
        Assert.Contains("offline-lessons.js", File.ReadAllText(Path.Combine(RepoRoot(), "src", "Vlms.Web", "Components", "App.razor")));
    }

    private static string WwwrootPath() => Path.Combine(RepoRoot(), "src", "Vlms.Web", "wwwroot");

    /// <summary>
    /// Walks up from the test assembly's output directory to find the repository root
    /// (identified by Vlms.slnx), since this suite reads Vlms.Web's static assets directly off
    /// disk rather than via a project reference (Vlms.Tests doesn't reference Vlms.Web).
    /// </summary>
    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Vlms.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.True(directory is not null, "Could not locate repository root (Vlms.slnx) from test output directory");
        return directory!.FullName;
    }
}
