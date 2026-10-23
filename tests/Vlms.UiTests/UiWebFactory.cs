using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Vlms.Domain;
using Vlms.Infrastructure;
using Vlms.Infrastructure.Authorization;
using Vlms.Infrastructure.Notifications;
using Vlms.Infrastructure.Storage;

namespace Vlms.UiTests;

/// <summary>
/// Boots the real Vlms.Web pipeline on a real Kestrel port (<c>UseKestrel(0)</c>, dynamic port) so a
/// browser can drive it. Trimmed copy of <c>Vlms.Tests.EndToEnd.VlmsWebFactory</c>: PostgreSQL becomes
/// SQLite in-memory, email goes nowhere, blob storage and the Data Protection key ring use a temp
/// directory. Referencing the test project instead would drag its whole assembly in for one class.
/// The entry-point type is the public <c>App</c> root component (Program is internal).
/// </summary>
public sealed class UiWebFactory : WebApplicationFactory<Vlms.Web.Components.App>
{
    private readonly SqliteConnection _anchor;
    private readonly string _connectionString;

    public string TempRoot { get; } = Path.Combine(Path.GetTempPath(), "vlms-ui-" + Guid.NewGuid().ToString("N"));
    public string BlobRoot => Path.Combine(TempRoot, "blobs");

    public UiWebFactory()
    {
        Directory.CreateDirectory(BlobRoot);
        _connectionString = $"Data Source=file:vlms-ui-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        _anchor = new SqliteConnection(_connectionString);
        _anchor.Open();
        // Must precede StartServer()/CreateClient() (Microsoft Learn: WebApplicationFactory<TEntryPoint>.UseKestrel(Int32)).
        UseKestrel(0);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Storage:LocalRootPath"] = BlobRoot,
            ["DataProtection:KeysPath"] = Path.Combine(TempRoot, "keys"),
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

            services.RemoveAll<IBlobStorage>();
            services.AddScoped<IBlobStorage>(_ => new LocalDiskBlobStorage(BlobRoot));

            services.RemoveAll<IEmailSender>();
            services.AddSingleton<IEmailSender, NullEmailSender>();
        });
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        var host = base.CreateHost(builder);
        using var scope = host.Services.CreateScope();
        var options = scope.ServiceProvider.GetRequiredService<DbContextOptions<VlmsDbContext>>();
        using var db = new VlmsDbContext(options, new SchemaUserContext());
        db.Database.EnsureCreated();
        return host;
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

    private sealed class NullEmailSender : IEmailSender
    {
        public Task SendAsync(string recipientEmail, string recipientName, string subject, string body, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    private sealed class SchemaUserContext : ICurrentUserContext
    {
        public int? UserId => 1;
        public bool HasRole(Role role) => role == Role.Admin;
        public bool HasMfaEnabled => true;
    }
}
