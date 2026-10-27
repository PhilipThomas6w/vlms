using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Vlms.Domain;
using Vlms.Infrastructure;
using Vlms.Infrastructure.Guardianship;
using Vlms.Infrastructure.Identity;

namespace Vlms.Tests.Infrastructure;

/// <summary>
/// Shared DI wiring for tests that exercise <see cref="GuardianLinkService"/> (and anything built
/// on it — <c>StudentRegistrationService</c>) after STATE.md's "Identity" item wired guardian-invite
/// issuance directly into <see cref="GuardianLinkService"/>'s two entry points. Every existing
/// <c>GuardianLinkServiceTests</c>/<c>StudentRegistrationServiceTests</c> seed guardian never has
/// <c>ParentGuardian.AppUserId</c> set — so <see cref="GuardianLinkService"/>'s new invite-issuance
/// call fires for essentially every one of those tests, not just new ones added for this item. This
/// factory wires the real DI shape (matching Vlms.Web's Program.cs, not a hand-rolled shortcut) —
/// <see cref="AppUserProvisioningService"/> needs a genuinely constructed
/// <c>UserManager&lt;VlmsIdentityUser&gt;</c>, which is impractical to hand-construct outside DI —
/// using <see cref="EphemeralDataProtectionProvider"/> (in-memory keys, no disk I/O — appropriate for
/// a test process) and <see cref="FakeNotificationService"/> so invite emails are captured, not
/// actually sent.
/// </summary>
internal static class TestIdentityServices
{
    /// <summary>
    /// <paramref name="publicBaseUrl"/> defaults to a valid value so every pre-existing test keeps
    /// exercising the happy path unchanged. Pass <c>null</c> only to reproduce
    /// <c>GuardianInviteService.SendInviteEmailAsync</c>'s real "App:PublicBaseUrl is not configured"
    /// failure mode — used by <c>GuardianLinkServiceTests</c>' invite-issuance-failure atomicity
    /// tests (checker-FAIL round-trip on STATE.md's "Identity" item) to prove the link/guardian
    /// write rolls back when the invite-issuance step fails, without adding an artificial fault-
    /// injection knob to <see cref="FakeNotificationService"/> for a failure this codebase can
    /// already trigger for real.
    /// </summary>
    public static ServiceProvider BuildServiceProvider(
        string connectionString, ICurrentUserContext currentUser, string? publicBaseUrl = "https://test.example",
        Action<IServiceCollection>? configure = null)
    {
        var services = new ServiceCollection();
        AddTo(services, connectionString, currentUser, publicBaseUrl);
        configure?.Invoke(services);

        return services.BuildServiceProvider();
    }

    /// <summary>The same registrations, into an existing collection (a bUnit test context's services).</summary>
    public static void AddTo(
        IServiceCollection services, string connectionString, ICurrentUserContext currentUser, string? publicBaseUrl = "https://test.example")
    {
        services.AddLogging();
        services.AddSingleton<ICurrentUserContext>(currentUser);
        services.AddDbContext<VlmsDbContext>(options => options.UseSqlite(connectionString));
        services.AddSingleton<IDataProtectionProvider, EphemeralDataProtectionProvider>();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection(publicBaseUrl is null
                ? new Dictionary<string, string?>()
                : new Dictionary<string, string?> { ["App:PublicBaseUrl"] = publicBaseUrl })
            .Build());
        services.AddSingleton<FakeNotificationService>();
        services.AddSingleton<Vlms.Domain.INotificationService>(sp => sp.GetRequiredService<FakeNotificationService>());
        // Vlms.Web uses AddDefaultTokenProviders (web-only Identity assembly); EmailTokenProvider from
        // Identity.Core stands in as the "Default" provider so reset/confirmation tokens work here.
        services.AddIdentityCore<VlmsIdentityUser>()
            .AddEntityFrameworkStores<VlmsDbContext>()
            .AddTokenProvider<EmailTokenProvider<VlmsIdentityUser>>(TokenOptions.DefaultProvider)
            // The real password rules, as Program.cs registers them (adr/0007).
            .AddVlmsPasswordRules();
        services.AddScoped<AppUserProvisioningService>();
        services.AddScoped<GuardianInviteService>();
        services.AddScoped<GuardianLinkService>();
        services.AddScoped<AccountEmailService>();
        services.AddScoped<StudentAccountService>();
    }
}
