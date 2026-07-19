using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Vlms.Infrastructure;
using Vlms.Infrastructure.Security;

namespace Vlms.Tests.Infrastructure;

/// <summary>
/// adr/0004-sensitive-data-access-control.md §4's database-level tamper protection
/// (<c>DENY UPDATE</c>/<c>DELETE</c> on <c>SensitiveDataAccessLogs</c>) is raw SQL Server-specific
/// T-SQL, added via <c>MigrationBuilder.Sql()</c> in the
/// <c>DenyUpdateDeleteOnSensitiveDataAccessLogs</c> migration. It cannot be exercised against the
/// SQLite in-memory provider the rest of this test suite uses (SQLite has no <c>DENY</c>/database
/// principal model, and this codebase's tests build schema via <c>Database.EnsureCreated()</c>
/// rather than running migrations at all — see openwiki/access-control.md). This is the honest
/// substitute: it generates the real migration SQL (the same code path
/// <c>dotnet ef migrations script</c> uses, via EF Core's own <see cref="IMigrator"/> service) and
/// asserts the generated script actually contains the expected <c>DENY</c> statement, targeting the
/// right table and only the right permissions — a genuine, if narrow, regression guard rather than
/// nothing. It does not and cannot prove the statement executes correctly against a live SQL
/// Server; that remains "structurally correct, not yet exercised against a live resource", the same
/// status documented for `AzureBlobStorage`/`AzureCommunicationEmailSender`/`EntraCurrentUserContext`
/// before their live dependencies existed.
/// </summary>
public class MigrationsTests
{
    private static string GenerateFullMigrationScript()
    {
        var options = new DbContextOptionsBuilder<VlmsDbContext>()
            .UseSqlServer("Server=(localdb)\\mssqllocaldb;Database=VlmsMigrationScriptCheck;Trusted_Connection=True;TrustServerCertificate=True")
            .Options;

        using var context = new VlmsDbContext(options, new NullCurrentUserContext());
        var migrator = context.GetService<IMigrator>();

        // No live connection is opened or required — GenerateScript works purely from the
        // registered migrations' metadata.
        return migrator.GenerateScript();
    }

    [Fact]
    public void MigrationScript_DeniesUpdateAndDeleteOnSensitiveDataAccessLogs()
    {
        var script = GenerateFullMigrationScript();

        Assert.Contains(
            "DENY UPDATE, DELETE ON dbo.SensitiveDataAccessLogs TO VlmsAppRole",
            script);
    }

    [Fact]
    public void MigrationScript_DoesNotDenyInsertOnSensitiveDataAccessLogs()
    {
        // The DENY must never block the SensitiveDataAuditInterceptor's own INSERT path — only
        // UPDATE/DELETE are named by adr/0004 §4 and security-compliance.md. Guards against
        // accidentally widening the DENY to INSERT (which would silently break audit logging
        // itself) or to TRUNCATE (never specified anywhere in the docs).
        var script = GenerateFullMigrationScript();

        Assert.DoesNotContain("DENY INSERT", script);
        Assert.DoesNotContain("TRUNCATE", script);
    }

    [Fact]
    public void MigrationScript_CreatesTheRoleBeforeDenyingOnIt()
    {
        var script = GenerateFullMigrationScript();

        var roleCreateIndex = script.IndexOf("CREATE ROLE VlmsAppRole", StringComparison.Ordinal);
        var denyIndex = script.IndexOf("DENY UPDATE, DELETE ON dbo.SensitiveDataAccessLogs", StringComparison.Ordinal);

        Assert.True(roleCreateIndex >= 0, "Expected the script to create the VlmsAppRole role.");
        Assert.True(denyIndex >= 0, "Expected the script to DENY on the VlmsAppRole role.");
        Assert.True(roleCreateIndex < denyIndex, "The role must be created before it is denied permissions.");
    }
}
