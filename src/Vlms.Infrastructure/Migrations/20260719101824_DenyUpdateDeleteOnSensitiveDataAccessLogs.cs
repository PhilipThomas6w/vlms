using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vlms.Infrastructure.Migrations
{
    /// <summary>
    /// adr/0004-sensitive-data-access-control.md §4 / data-design.md's
    /// <c>SensitiveDataAccessLog</c> doc comment: "UPDATE/DELETE denied at the database permission
    /// level (not just an application-level convention)". Raw T-SQL (SQL Server-specific
    /// <c>DENY</c> DDL) — never applied against the SQLite in-memory provider the test suite uses,
    /// since tests build their schema via <c>Database.EnsureCreated()</c> from the current model,
    /// not by running migrations (confirmed: no test in this codebase calls
    /// <c>Database.Migrate()</c>, and neither <c>Vlms.Web</c> nor <c>Vlms.Jobs</c> calls it either
    /// — migrations are applied only via an explicit <c>dotnet ef database update</c> against a
    /// real SQL Server target). No entity/model changes at all, so this migration is pure DDL — see
    /// openwiki/access-control.md for how this was verified.
    ///
    /// The app's runtime SQL principal is not yet named anywhere in this codebase (local dev uses
    /// a Trusted_Connection/Windows-auth localdb connection string; the production Azure SQL
    /// principal — a SQL login or an Azure AD/Managed Identity contained user — is not decided
    /// until a real Azure SQL Database exists, per the "no live Azure environment yet" status
    /// already documented for `AzureBlobStorage`/`EntraCurrentUserContext`/Communication Services).
    /// Rather than guessing a concrete principal name that deployment might not match, the DENY is
    /// applied to a dedicated database role, <c>VlmsAppRole</c>, created here — whichever concrete
    /// principal ends up backing `ConnectionStrings:VlmsDatabase` must be added as a member of this
    /// role as a one-time provisioning step when the real Azure SQL Database is set up (documented
    /// in adr/0004 and openwiki/access-control.md). This also keeps the DENY safe to re-apply: role
    /// creation is guarded by an existence check, and DENY itself is idempotent (re-issuing the
    /// same DENY does not error).
    /// </summary>
    public partial class DenyUpdateDeleteOnSensitiveDataAccessLogs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
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

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "REVOKE UPDATE, DELETE ON dbo.SensitiveDataAccessLogs TO VlmsAppRole;");

            migrationBuilder.Sql(
                """
                IF EXISTS (SELECT 1 FROM sys.database_principals WHERE name = 'VlmsAppRole' AND type = 'R')
                BEGIN
                    DROP ROLE VlmsAppRole;
                END
                """);
        }
    }
}
