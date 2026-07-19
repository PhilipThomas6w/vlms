using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vlms.Infrastructure.Migrations
{
    /// <summary>
    /// Supersedes the earlier <c>DenyUpdateDeleteOnSensitiveDataAccessLogs</c> migration's
    /// tamper-protection mechanism (adr/0004-sensitive-data-access-control.md §4). That migration
    /// denied <c>UPDATE</c>/<c>DELETE</c> on <c>dbo.SensitiveDataAccessLogs</c> to a dedicated
    /// database role, <c>VlmsAppRole</c>, that principals had to be explicitly added to. That role
    /// starts with zero members, so the DENY was <em>inert until</em> a deployer remembered to add
    /// the production SQL principal to it — the migration could run cleanly and the audit log still
    /// be fully mutable, silently. A tamper-protection DENY should apply as broadly as possible;
    /// scoping it to an opt-in role inverted that intent.
    ///
    /// This migration re-targets the DENY to the <c>public</c> database role instead. Per Microsoft
    /// Learn (Database-Level Roles: "Public database role"), <em>every</em> database user belongs to
    /// <c>public</c> and cannot be removed from it, so <c>DENY ... TO public</c> denies every current
    /// and future database principal automatically — nothing to remember, nothing to provision. Per
    /// Microsoft Learn (DENY (Transact-SQL)): "DENY takes precedence over all permissions, except
    /// that DENY does not apply to object owners or members of the sysadmin fixed server role." So
    /// the only principals that can still <c>UPDATE</c>/<c>DELETE</c> the log are the object owner
    /// (<c>dbo</c>, which owns <c>dbo.SensitiveDataAccessLogs</c>) and sysadmin — in Azure SQL, the
    /// server-level principal / Microsoft Entra admin. Those are the unavoidable DBA-level identities
    /// SQL Server exempts by design; the application's runtime principal must not be one of them.
    ///
    /// Residual requirement (tracked in governance/raid.md D-004): the app must connect as a
    /// least-privilege contained user (e.g. db_datareader + db_datawriter), never as db_owner / the
    /// object owner / the server admin — otherwise it bypasses this DENY. Any future retention-purge
    /// of expired audit rows (the separate, not-yet-built 6-year retention job) must therefore run as
    /// a deliberately elevated principal, which is the correct posture for mutating a tamper-proof log.
    ///
    /// <c>INSERT</c> is still not denied (the <c>SensitiveDataAuditInterceptor</c>'s own writes must
    /// keep working) and <c>TRUNCATE</c> is not denied either (named nowhere in the ADR or
    /// governance/security-compliance.md). This is raw SQL Server-specific T-SQL, never applied
    /// against the SQLite in-memory provider the test suite uses (tests build schema via
    /// <c>Database.EnsureCreated()</c>, never <c>Database.Migrate()</c>); verified via
    /// tests/Vlms.Tests/Infrastructure/MigrationsTests.cs, which generates the real migration SQL
    /// through EF Core's <c>IMigrator.GenerateScript()</c> and asserts the DENY now targets
    /// <c>public</c> and that the superseded <c>VlmsAppRole</c> is dropped. No entity/model changes —
    /// pure DDL. The <c>VlmsAppRole</c> role is dropped entirely (it has no members and owns nothing,
    /// so <c>DROP ROLE</c> succeeds), since with the DENY on <c>public</c> it serves no purpose and a
    /// dangling zero-member role would only mislead a future reader into thinking membership matters.
    /// </summary>
    public partial class SupersedeVlmsAppRoleWithPublicDenyOnSensitiveDataAccessLogs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Drop the superseded role. It has no members and owns no securables, so DROP ROLE
            // succeeds; dropping it also removes the earlier migration's DENY that targeted it.
            migrationBuilder.Sql(
                """
                IF EXISTS (SELECT 1 FROM sys.database_principals WHERE name = 'VlmsAppRole' AND type = 'R')
                BEGIN
                    DROP ROLE VlmsAppRole;
                END
                """);

            // Deny to public — held implicitly by every database user, so the protection can never be
            // left inert by a forgotten role-membership step. DENY is idempotent to re-issue.
            migrationBuilder.Sql(
                "DENY UPDATE, DELETE ON dbo.SensitiveDataAccessLogs TO public;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Reverse to the earlier migration's end state: DENY scoped to VlmsAppRole.
            migrationBuilder.Sql(
                "REVOKE UPDATE, DELETE ON dbo.SensitiveDataAccessLogs TO public;");

            migrationBuilder.Sql(
                """
                IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = 'VlmsAppRole' AND type = 'R')
                BEGIN
                    CREATE ROLE VlmsAppRole AUTHORIZATION dbo;
                END
                """);

            migrationBuilder.Sql(
                "DENY UPDATE, DELETE ON dbo.SensitiveDataAccessLogs TO VlmsAppRole;");
        }
    }
}
