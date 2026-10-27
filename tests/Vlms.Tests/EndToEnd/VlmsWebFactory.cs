using System.Net;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Internal;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Vlms.Domain;
using Vlms.Infrastructure;
using Vlms.Infrastructure.Identity;
using Vlms.Infrastructure.Notifications;
using Vlms.Infrastructure.Retention;
using Vlms.Infrastructure.Storage;
using Vlms.Tests.Infrastructure;

namespace Vlms.Tests.EndToEnd;

/// <summary>
/// Boots the real Vlms.Web pipeline (Program.cs, middleware, endpoints, Razor components, Identity)
/// in-process. Only the edges are replaced: PostgreSQL becomes SQLite in-memory (schema from
/// <c>EnsureCreated</c>, so PostgreSQL-only migration SQL such as the audit-log row-level security is
/// NOT exercised), the Graph email sender becomes <see cref="FakeEmailSender"/>, and blob storage
/// and the Data Protection key ring point at a temp directory. The entry-point type is the public
/// <c>App</c> root component: it only identifies the Vlms.Web assembly, since the top-level
/// <c>Program</c> class is internal.
/// </summary>
public sealed class VlmsWebFactory : WebApplicationFactory<Vlms.Web.Components.App>
{
    public const string Password = "Correct-Horse-9!";

    private readonly SqliteConnection _anchor;
    private readonly string _connectionString;
    private int _nextUserId = 1000;

    public string TempRoot { get; } = Path.Combine(Path.GetTempPath(), "vlms-e2e-" + Guid.NewGuid().ToString("N"));
    public string BlobRoot => Path.Combine(TempRoot, "blobs");
    public FakeEmailSender Email { get; } = new();

    /// <summary>
    /// When set before the host starts, replaces Program.cs's 1-minute security-stamp validation
    /// interval, so a test can see an existing session re-validated on its very next request.
    /// </summary>
    public TimeSpan? StampValidationInterval { get; init; }

    public VlmsWebFactory()
    {
        Directory.CreateDirectory(BlobRoot);
        _connectionString = $"Data Source=file:vlms-e2e-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        _anchor = new SqliteConnection(_connectionString);
        _anchor.Open();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Storage:LocalRootPath"] = BlobRoot,
            ["DataProtection:KeysPath"] = Path.Combine(TempRoot, "keys"),
            // Many sign-ins share one (null) client IP in the test server; keep the limiter out of the way.
            ["RateLimiting:AccountFormPostsPerMinute"] = "100000",
            ["App:PublicBaseUrl"] = "https://test.example",
        }));

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<VlmsDbContext>>();
            // EF Core keeps the AddDbContext(o => o.UseNpgsql(...)) callback in an internal
            // IDbContextOptionsConfiguration<VlmsDbContext> registration; drop it by name or the
            // Npgsql provider would be configured alongside SQLite.
            foreach (var descriptor in services.Where(d =>
                         d.ServiceType.IsGenericType
                         && d.ServiceType.GetGenericArguments().SequenceEqual([typeof(VlmsDbContext)])
                         && d.ServiceType.Name.StartsWith("IDbContextOptionsConfiguration", StringComparison.Ordinal)).ToList())
            {
                services.Remove(descriptor);
            }
            services.RemoveAll<VlmsDbContext>();
            services.AddDbContext<VlmsDbContext>(o => o.UseSqlite(_connectionString));

            // Program.cs resolves Storage:LocalRootPath while it runs, before the factory's configuration
            // overrides are visible to it, so the blob store is pointed at the temp directory here too.
            services.RemoveAll<IBlobStorage>();
            services.AddScoped<IBlobStorage>(_ => new LocalDiskBlobStorage(BlobRoot));

            services.RemoveAll<IEmailSender>();
            services.AddSingleton<IEmailSender>(Email);

            if (StampValidationInterval is { } interval)
            {
                services.PostConfigure<SecurityStampValidatorOptions>(o => o.ValidationInterval = interval);
            }
        });
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        var host = base.CreateHost(builder);
        using var scope = host.Services.CreateScope();
        var options = scope.ServiceProvider.GetRequiredService<DbContextOptions<VlmsDbContext>>();
        using var db = new VlmsDbContext(options, new FakeCurrentUserContext(1, Role.Admin));
        db.Database.EnsureCreated();
        return host;
    }

    /// <summary>A client that behaves like a browser session: keeps cookies, never follows redirects.</summary>
    public HttpClient CreateBrowser() => CreateClient(new WebApplicationFactoryClientOptions
    {
        AllowAutoRedirect = false,
        HandleCookies = true,
    });

    /// <summary>Runs <paramref name="work"/> against a context with full access (test seeding only).</summary>
    public async Task WithDbAsync(Func<VlmsDbContext, Task> work)
    {
        using var scope = Services.CreateScope();
        var options = scope.ServiceProvider.GetRequiredService<DbContextOptions<VlmsDbContext>>();
        await using var db = new VlmsDbContext(options, new FakeCurrentUserContext(1, Role.Admin, Role.SafeguardingOfficer));
        await work(db);
    }

    /// <summary>
    /// Creates a confirmed-email account (Identity user + paired AppUser + user_roles rows). With
    /// <paramref name="withTotp"/> an authenticator key is set and two-factor enabled, and the key is returned.
    /// </summary>
    public async Task<SeededUser> SeedUserAsync(string email, bool withTotp, params Role[] roles)
    {
        using var scope = Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<VlmsIdentityUser>>();
        var id = Interlocked.Increment(ref _nextUserId);
        var user = new VlmsIdentityUser { Id = id, UserName = email, Email = email, EmailConfirmed = true };
        var created = await users.CreateAsync(user, Password);
        Assert.True(created.Succeeded, string.Join("; ", created.Errors.Select(e => e.Description)));

        string? key = null;
        if (withTotp)
        {
            await users.ResetAuthenticatorKeyAsync(user);
            key = await users.GetAuthenticatorKeyAsync(user);
            Assert.False(string.IsNullOrEmpty(key));
            Assert.True((await users.SetTwoFactorEnabledAsync(user, true)).Succeeded);
        }

        await WithDbAsync(async db =>
        {
            db.AppUsers.Add(new AppUser { Id = user.Id, DisplayName = email, Email = email });
            foreach (var role in roles)
            {
                db.UserRoles.Add(new UserRole { UserId = user.Id, Role = role });
            }

            await db.SaveChangesAsync();
        });

        return new SeededUser(user.Id, email, key);
    }

    /// <summary>
    /// Runs the real <see cref="LeavingService.StepDownAsync"/> against this app's database, as
    /// the Admin <paramref name="adminUserId"/> (the page that calls it is interactive, so it can't be
    /// driven by a form post here).
    /// </summary>
    public async Task<AppUser> StepDownAsync(int adminUserId, int userId)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ICurrentUserContext>(new FakeCurrentUserContext(adminUserId, Role.Admin));
        services.AddDbContext<VlmsDbContext>(o => o.UseSqlite(_connectionString));
        services.AddIdentityCore<VlmsIdentityUser>().AddEntityFrameworkStores<VlmsDbContext>();
        services.AddScoped<LeavingService>();
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<LeavingService>().StepDownAsync(userId);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            _anchor.Dispose();
            try { Directory.Delete(TempRoot, recursive: true); } catch (IOException) { }
        }
    }
}

public sealed record SeededUser(int Id, string Email, string? AuthenticatorKey);

/// <summary>Drives the static-SSR account forms like a browser, and computes TOTP codes.</summary>
public static partial class Browser
{
    [GeneratedRegex("<input[^>]*__RequestVerificationToken[^>]*>")]
    private static partial Regex TokenTagPattern();

    [GeneratedRegex("value=\"([^\"]*)\"")]
    private static partial Regex ValueAttributePattern();

    public static async Task<string> GetAntiforgeryTokenAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        var tag = TokenTagPattern().Match(html);
        Assert.True(tag.Success, $"No antiforgery token input on {url}");
        var value = ValueAttributePattern().Match(tag.Value);
        Assert.True(value.Success, $"Antiforgery token input has no value on {url}");
        return WebUtility.HtmlDecode(value.Groups[1].Value);
    }

    /// <summary>POSTs the static-SSR login form. Returns the raw response (no redirects followed).</summary>
    public static async Task<HttpResponseMessage> PostLoginAsync(HttpClient client, string userName, string password)
    {
        var token = await GetAntiforgeryTokenAsync(client, "/Account/Login");
        return await client.PostAsync("/Account/Login", new FormUrlEncodedContent(
        [
            KeyValuePair.Create("_handler", "login"),
            KeyValuePair.Create("__RequestVerificationToken", token),
            KeyValuePair.Create("Input.UserName", userName),
            KeyValuePair.Create("Input.Password", password),
        ]));
    }

    /// <summary>POSTs the 2FA form at the URL the login redirect named.</summary>
    public static async Task<HttpResponseMessage> PostTwoFactorAsync(HttpClient client, string url, string code)
    {
        var token = await GetAntiforgeryTokenAsync(client, url);
        return await client.PostAsync(url, new FormUrlEncodedContent(
        [
            KeyValuePair.Create("_handler", "login-with-2fa"),
            KeyValuePair.Create("__RequestVerificationToken", token),
            KeyValuePair.Create("Input.TwoFactorCode", code),
        ]));
    }

    /// <summary>Full sign-in: password, then the TOTP step when the account has one. Asserts it ends in a 302 to "/".</summary>
    public static async Task<HttpClient> SignInAsync(VlmsWebFactory factory, SeededUser user)
    {
        var client = factory.CreateBrowser();
        var login = await PostLoginAsync(client, user.Email, VlmsWebFactory.Password);
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        if (user.AuthenticatorKey is not null)
        {
            var twoFactorUrl = RelativeLocation(login);
            Assert.StartsWith("/Account/LoginWith2fa", twoFactorUrl, StringComparison.OrdinalIgnoreCase);
            var second = await PostTwoFactorAsync(client, twoFactorUrl, Totp(user.AuthenticatorKey));
            Assert.Equal(HttpStatusCode.Redirect, second.StatusCode);
            Assert.Equal("/", RelativeLocation(second));
        }
        else
        {
            Assert.Equal("/", RelativeLocation(login));
        }

        return client;
    }

    /// <summary>The redirect target as a rooted path and query.</summary>
    public static string RelativeLocation(HttpResponseMessage response)
    {
        var location = response.Headers.Location;
        Assert.NotNull(location);
        var text = location!.IsAbsoluteUri ? location.PathAndQuery : location.OriginalString;
        return text.StartsWith('/') ? text : "/" + text;
    }

    /// <summary>RFC 6238, SHA-1, 6 digits, 30 s step: what Identity's authenticator token provider validates.</summary>
    public static string Totp(string base32Key, DateTimeOffset? at = null)
    {
        var key = FromBase32(base32Key);
        var step = (at ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds() / 30;
        var counter = new byte[8];
        for (var i = 7; i >= 0; i--)
        {
            counter[i] = (byte)(step & 0xFF);
            step >>= 8;
        }

        var hash = HMACSHA1.HashData(key, counter);
        var offset = hash[^1] & 0x0F;
        var binary = ((hash[offset] & 0x7F) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (binary % 1_000_000).ToString("D6");
    }

    private static byte[] FromBase32(string input)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var bytes = new List<byte>();
        var buffer = 0;
        var bits = 0;
        foreach (var c in input.TrimEnd('=').ToUpperInvariant())
        {
            var value = alphabet.IndexOf(c);
            Assert.True(value >= 0, "Not a base32 character: " + c);
            buffer = (buffer << 5) | value;
            bits += 5;
            if (bits >= 8)
            {
                bytes.Add((byte)((buffer >> (bits - 8)) & 0xFF));
                bits -= 8;
            }
        }

        return bytes.ToArray();
    }
}
