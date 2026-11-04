using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Vlms.Tests.EndToEnd;
using Vlms.Web;
using Xunit;

namespace Vlms.Tests.Web;

/// <summary>
/// <c>GET /.well-known/assetlinks.json</c> (Play Store Trusted Web Activity, adr/0011): 404 until configured;
/// once configured, the Digital Asset Links statement in the documented format
/// (https://developer.android.com/training/app-links/configure-assetlinks), anonymous, as
/// <c>application/json</c>, with no redirect; malformed configuration is refused rather than published.
/// </summary>
public sealed class AssetLinksEndpointTests
{
    private const string Fingerprint =
        "14:6D:E9:83:C5:73:06:50:D8:EE:B9:95:2F:34:FC:64:16:A0:83:42:E6:1D:BE:A8:8A:04:96:B2:3F:CF:44:E5";
    private const string SecondFingerprint =
        "AA:BB:CC:DD:EE:FF:00:11:22:33:44:55:66:77:88:99:AA:BB:CC:DD:EE:FF:00:11:22:33:44:55:66:77:88:99";

    [Fact]
    public async Task NotConfigured_Is404()
    {
        using var factory = new VlmsWebFactory();

        var response = await factory.CreateBrowser().GetAsync(AssetLinksEndpoint.Path);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Configured_AnonymousGet_ServesTheStatement_AsJson_WithoutRedirect()
    {
        using var factory = new VlmsWebFactory();
        using var configured = factory.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                [AssetLinksEndpoint.PackageNameKey] = "uk.co.varangians.app",
                [AssetLinksEndpoint.FingerprintsKey] = $"{Fingerprint.ToLowerInvariant()}, {SecondFingerprint}",
            })));

        var client = configured.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var response = await client.GetAsync(AssetLinksEndpoint.Path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var statement = Assert.Single(document.RootElement.EnumerateArray());
        Assert.Equal("delegate_permission/common.handle_all_urls", Assert.Single(statement.GetProperty("relation").EnumerateArray()).GetString());
        var target = statement.GetProperty("target");
        Assert.Equal("android_app", target.GetProperty("namespace").GetString());
        Assert.Equal("uk.co.varangians.app", target.GetProperty("package_name").GetString());
        Assert.Equal(
            [Fingerprint, SecondFingerprint],
            target.GetProperty("sha256_cert_fingerprints").EnumerateArray().Select(f => f.GetString()!).ToArray());
    }

    [Fact]
    public void NeitherSet_IsNotServed_AndIsNotAProblem()
    {
        Assert.Null(AssetLinksEndpoint.BuildStatement(null, " ", out var problem));
        Assert.Null(problem);
    }

    [Theory]
    [InlineData("uk.co.varangians.app", null)]
    [InlineData(null, Fingerprint)]
    [InlineData("varangians", Fingerprint)]
    [InlineData("uk.co.1varangians", Fingerprint)]
    [InlineData("uk.co.varangians.app", "14:6D:E9")]
    [InlineData("uk.co.varangians.app", "146DE983C5730650D8EEB9952F34FC6416A08342E61DBEA88A0496B23FCF44E5")]
    [InlineData("uk.co.varangians.app", Fingerprint + ",not-a-fingerprint")]
    [InlineData("uk.co.varangians.app\"},{\"x\":\"", Fingerprint)]
    public void Misconfigured_IsNotServed_AndSaysWhy(string? packageName, string? fingerprints)
    {
        Assert.Null(AssetLinksEndpoint.BuildStatement(packageName, fingerprints, out var problem));
        Assert.False(string.IsNullOrWhiteSpace(problem));
    }

    [Fact]
    public void DuplicateFingerprints_AreListedOnce()
    {
        var json = AssetLinksEndpoint.BuildStatement("uk.co.varangians.app", $"{Fingerprint},{Fingerprint.ToLowerInvariant()}", out _);

        using var document = JsonDocument.Parse(json!);
        Assert.Single(document.RootElement[0].GetProperty("target").GetProperty("sha256_cert_fingerprints").EnumerateArray());
    }
}
