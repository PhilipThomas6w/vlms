using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Vlms.Infrastructure;
using Vlms.Infrastructure.Security;

namespace Vlms.Tests.Infrastructure;

/// <summary>
/// adr/0004-sensitive-data-access-control.md §4's database-level tamper protection
/// (<c>REVOKE UPDATE</c>/<c>DELETE ... FROM PUBLIC</c>) is raw PostgreSQL-specific SQL, added via
/// <c>MigrationBuilder.Sql()</c> in the <c>RevokeUpdateDeleteOnSensitiveDataAccessLog</c> migration.
/// This replaces the two SQL-Server-specific migrations from before
/// adr/0005-hosting-pivot-self-hosted-vps-postgresql.md (<c>DenyUpdateDeleteOnSensitiveDataAccessLogs</c>
/// / <c>SupersedeVlmsAppRoleWithPublicDenyOnSensitiveDataAccessLogs</c>, both deleted when the
/// migration history was regenerated for Npgsql) — their full history and rationale remain recorded
/// in ADR-0004 §4/Alternatives, which is not re-enacted here.
///
/// It cannot be exercised against the SQLite in-memory provider the rest of this test suite uses
/// (SQLite has no <c>REVOKE</c>/database-role model, and this codebase's tests build schema via
/// <c>Database.EnsureCreated()</c> rather than running migrations at all — see
/// openwiki/access-control.md and openwiki/testing.md's "test-provider strategy" section). This is
/// the honest substitute: it generates the real migration SQL (the same code path
/// <c>dotnet ef migrations script</c> uses, via EF Core's own <see cref="IMigrator"/> service,
/// against a context configured with the real Npgsql provider) and asserts the generated script
/// targets the right table and only the right permissions — a genuine, if narrow, regression guard
/// rather than nothing. It does not and cannot prove the statement executes correctly against a live
/// PostgreSQL server; that remains "structurally correct, not yet exercised against a live resource",
/// the same status documented for `AzureBlobStorage`/`EntraCurrentUserContext` before their live
/// dependencies existed. It also cannot and does not prove the REVOKE achieves SQL Server DENY's
/// override semantics — it verified doesn't, per the migration's own doc comment; this test only
/// locks in that the literal, disclosed-as-partial statement is present.
/// </summary>
public class MigrationsTests
{
    private static string GenerateFullMigrationScript()
    {
        var options = new DbContextOptionsBuilder<VlmsDbContext>()
            .UseNpgsql("Host=localhost;Database=vlms_migration_script_check;Username=vlms_migration_script_check;Password=PLACEHOLDER")
            .Options;

        using var context = new VlmsDbContext(options, new NullCurrentUserContext());
        var migrator = context.GetService<IMigrator>();

        // No live connection is opened or required — GenerateScript works purely from the
        // registered migrations' metadata.
        return migrator.GenerateScript();
    }

    [Fact]
    public void MigrationScript_RevokesUpdateAndDeleteFromPublic()
    {
        // The literal, disclosed-as-partial tamper-protection statement (see the migration's own
        // doc comment for exactly what it does and does not guarantee on PostgreSQL versus SQL
        // Server's DENY). Table name is snake_case per SnakeCaseNamingConvention.
        var script = GenerateFullMigrationScript();

        Assert.Contains(
            "REVOKE UPDATE, DELETE ON sensitive_data_access_logs FROM PUBLIC;",
            script);
    }

    [Fact]
    public void MigrationScript_DoesNotRevokeInsertOnSensitiveDataAccessLogs()
    {
        // The REVOKE must never block the SensitiveDataAuditInterceptor's own INSERT path — only
        // UPDATE/DELETE are named by adr/0004 §4 and security-compliance.md. Guards against
        // accidentally widening it to INSERT (which would silently break audit logging itself) or
        // to TRUNCATE (never specified anywhere in the docs).
        var script = GenerateFullMigrationScript();

        Assert.DoesNotContain("REVOKE INSERT", script);
        Assert.DoesNotContain("TRUNCATE", script);
    }

    [Fact]
    public void MigrationScript_RevokeIsAppliedAfterInitialCreate()
    {
        // The REVOKE must land after the table it targets actually exists — proving migration
        // ordering, not just that both fragments are present somewhere in the script.
        var script = GenerateFullMigrationScript();

        var createTableIndex = script.IndexOf("CREATE TABLE sensitive_data_access_logs", StringComparison.Ordinal);
        var revokeIndex = script.IndexOf(
            "REVOKE UPDATE, DELETE ON sensitive_data_access_logs FROM PUBLIC;",
            StringComparison.Ordinal);

        Assert.True(createTableIndex >= 0, "Expected InitialCreate to create the sensitive_data_access_logs table.");
        Assert.True(revokeIndex > createTableIndex, "The REVOKE must be applied after the table is created.");
    }
}
