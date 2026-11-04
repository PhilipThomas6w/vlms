using System.Text.Json;
using System.Text.RegularExpressions;

namespace Vlms.Web;

/// <summary>
/// <c>GET /.well-known/assetlinks.json</c>: the Digital Asset Links statement that lets the Play Store app
/// (a Trusted Web Activity wrapping this PWA; adr/0011, docs/roadmap.md) open the site full screen without a
/// browser bar. Format, content type and the no-redirect rule:
/// https://developer.android.com/training/app-links/configure-assetlinks.
/// <para>
/// Anonymous by design (Android fetches it without credentials) and exempt from antiforgery. The body is built
/// once at startup from configuration only: <c>PlayStore:PackageName</c> and
/// <c>PlayStore:CertificateSha256Fingerprints</c> (comma-separated; with Play App Signing, the app signing
/// key's fingerprint from Play Console, plus the upload key's if wanted). Both are public values: the
/// statement exists to be published. Until both are set and valid the path answers 404, so nothing claims a
/// link to an app that does not exist yet; a value set but malformed is logged once at startup and also
/// answers 404. No database or user data is read.
/// </para>
/// </summary>
public static partial class AssetLinksEndpoint
{
    public const string Path = "/.well-known/assetlinks.json";
    public const string PackageNameKey = "PlayStore:PackageName";
    public const string FingerprintsKey = "PlayStore:CertificateSha256Fingerprints";

    public static IEndpointConventionBuilder MapAssetLinksEndpoint(this IEndpointRouteBuilder endpoints)
    {
        var configuration = endpoints.ServiceProvider.GetRequiredService<IConfiguration>();
        var logger = endpoints.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(AssetLinksEndpoint));

        var body = BuildStatement(configuration[PackageNameKey], configuration[FingerprintsKey], out var problem);
        if (problem is not null)
        {
            logger.LogWarning("{Path} is not served: {Problem}", Path, problem);
        }

        return endpoints.MapGet(Path, () => body is null
                ? Results.NotFound()
                : Results.Text(body, "application/json"))
            .AllowAnonymous()
            .DisableAntiforgery();
    }

    /// <summary>
    /// The statement list as JSON, or null when not configured (problem null) or misconfigured (problem says why).
    /// Fingerprints are upper-cased; duplicates are dropped.
    /// </summary>
    public static string? BuildStatement(string? packageName, string? fingerprints, out string? problem)
    {
        problem = null;
        var hasPackage = !string.IsNullOrWhiteSpace(packageName);
        var hasFingerprints = !string.IsNullOrWhiteSpace(fingerprints);
        if (!hasPackage && !hasFingerprints)
        {
            return null;
        }
        if (!hasPackage || !hasFingerprints)
        {
            problem = $"set both {PackageNameKey} and {FingerprintsKey}, or neither.";
            return null;
        }

        packageName = packageName!.Trim();
        if (!PackageNamePattern().IsMatch(packageName))
        {
            problem = $"{PackageNameKey} is not an Android application ID (for example uk.co.example.app).";
            return null;
        }

        var list = fingerprints!
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(f => f.ToUpperInvariant())
            .Distinct()
            .ToList();
        if (list.Count == 0 || list.Any(f => !FingerprintPattern().IsMatch(f)))
        {
            problem = $"{FingerprintsKey} must be SHA-256 fingerprints as 32 colon-separated hex pairs, comma-separated.";
            return null;
        }

        return JsonSerializer.Serialize(new[]
        {
            new Dictionary<string, object>
            {
                ["relation"] = new[] { "delegate_permission/common.handle_all_urls" },
                ["target"] = new Dictionary<string, object>
                {
                    ["namespace"] = "android_app",
                    ["package_name"] = packageName,
                    ["sha256_cert_fingerprints"] = list,
                },
            },
        });
    }

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_]*(\.[A-Za-z][A-Za-z0-9_]*)+$")]
    private static partial Regex PackageNamePattern();

    [GeneratedRegex(@"^([0-9A-F]{2}:){31}[0-9A-F]{2}$")]
    private static partial Regex FingerprintPattern();
}
