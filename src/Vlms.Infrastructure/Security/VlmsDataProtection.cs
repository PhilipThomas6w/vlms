using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Vlms.Infrastructure.Security;

/// <summary>
/// Data Protection registration for Vlms.Web. Backs Identity's auth cookies and
/// <c>GuardianInviteService</c>'s signed invite tokens, so the key ring must survive a restart:
/// otherwise every restart signs everyone out and invalidates every in-flight invite link.
///
/// The default key-ring location is the OS user profile, but the <c>vlms</c> service user is
/// created with <c>--no-create-home</c> (deploy/README.md §1), so in production the default would
/// fall back to an in-memory, per-process key ring. <see cref="KeysPathKey"/> points the ring at a
/// directory instead (systemd's <c>StateDirectory=vlms/keys</c>, mode 0700, in vlms-web.service).
///
/// Keys are not encrypted at rest. On Linux the default location doesn't encrypt them either, and
/// Microsoft's guidance for a local directory is filesystem permissions or an X.509 certificate
/// (learn.microsoft.com/aspnet/core/security/data-protection/implementation/key-storage-providers);
/// a 0700 directory owned by the service user is the protection chosen for the single-VPS target.
///
/// <see cref="ApplicationName"/> is set explicitly so the key ring isn't scoped to the content-root
/// path: a redeploy to a different publish directory would otherwise orphan the existing keys
/// (learn.microsoft.com/aspnet/core/security/data-protection/configuration/overview#setapplicationname).
/// </summary>
public static class VlmsDataProtection
{
    public const string KeysPathKey = "DataProtection:KeysPath";
    public const string ApplicationName = "Vlms";

    /// <param name="requireKeysPath">
    /// True in Production: startup fails fast rather than silently running on an ephemeral key ring.
    /// Development may leave the path blank and use the framework default.
    /// </param>
    public static IDataProtectionBuilder AddVlmsDataProtection(
        this IServiceCollection services, IConfiguration configuration, bool requireKeysPath)
    {
        var builder = services.AddDataProtection().SetApplicationName(ApplicationName);

        var keysPath = configuration[KeysPathKey];
        if (string.IsNullOrWhiteSpace(keysPath))
        {
            if (requireKeysPath)
            {
                throw new InvalidOperationException(
                    $"{KeysPathKey} must be set in production so the Data Protection key ring survives restarts (see deploy/README.md).");
            }

            return builder;
        }

        return builder.PersistKeysToFileSystem(new DirectoryInfo(keysPath));
    }
}
