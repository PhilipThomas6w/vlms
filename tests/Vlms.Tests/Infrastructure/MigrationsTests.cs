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

    // --- EnforceRowLevelSecurityOnSensitiveDataAccessLog (adr/0004 §4/§5 pass 8) ---
    //
    // Same honest limits as the tests above: GenerateScript() proves the literal SQL text, clause
    // choice, and statement ordering are correct — the same code path `dotnet ef migrations script`
    // uses — not that a live PostgreSQL server executes them with the documented RLS semantics
    // (verified instead against current PostgreSQL documentation; see the migration's own doc
    // comment and adr/0004 §4 pass 8). This suite deliberately checks more than "the UPDATE/DELETE
    // gap is closed" — per this item's own instruction, it also proves the SELECT/INSERT permissive
    // policies are present with the right clause (USING for SELECT, WITH CHECK — not USING — for
    // INSERT), since a swapped clause would be exactly the kind of subtle mistake that looks
    // plausible but silently blocks the audit interceptor's own INSERT path at runtime.

    [Fact]
    public void MigrationScript_EnablesAndForcesRowLevelSecurity()
    {
        var script = GenerateFullMigrationScript();

        Assert.Contains(
            "ALTER TABLE sensitive_data_access_logs ENABLE ROW LEVEL SECURITY;",
            script);
        Assert.Contains(
            "ALTER TABLE sensitive_data_access_logs FORCE ROW LEVEL SECURITY;",
            script);
    }

    [Fact]
    public void MigrationScript_EnableIsAppliedBeforeForce()
    {
        // FORCE only has meaning once ENABLE has made row security apply to the table at all — the
        // migration's doc comment explains why both are required and in this order; this locks that
        // ordering in rather than trusting the source order alone.
        var script = GenerateFullMigrationScript();

        var enableIndex = script.IndexOf(
            "ALTER TABLE sensitive_data_access_logs ENABLE ROW LEVEL SECURITY;",
            StringComparison.Ordinal);
        var forceIndex = script.IndexOf(
            "ALTER TABLE sensitive_data_access_logs FORCE ROW LEVEL SECURITY;",
            StringComparison.Ordinal);

        Assert.True(enableIndex >= 0, "Expected ENABLE ROW LEVEL SECURITY to be present.");
        Assert.True(forceIndex > enableIndex, "FORCE must be applied after ENABLE.");
    }

    [Fact]
    public void MigrationScript_SelectPolicyUsesUsingNotWithCheck()
    {
        // SELECT has no row being written to validate — only USING (row visibility) is meaningful.
        var script = GenerateFullMigrationScript();

        Assert.Contains(
            "CREATE POLICY sensitive_data_access_logs_allow_select ON sensitive_data_access_logs FOR SELECT USING (true);",
            script);
    }

    [Fact]
    public void MigrationScript_InsertPolicyUsesWithCheckNotUsing()
    {
        // The specific risk this item's own instructions flagged: INSERT must be validated via
        // WITH CHECK, not USING (there is no existing row for INSERT to filter with USING). Assert
        // the exact clause, and separately assert the wrong clause is absent for this policy so a
        // future accidental "USING (true)" on the INSERT policy would fail this test.
        var script = GenerateFullMigrationScript();

        Assert.Contains(
            "CREATE POLICY sensitive_data_access_logs_allow_insert ON sensitive_data_access_logs FOR INSERT WITH CHECK (true);",
            script);
        Assert.DoesNotContain(
            "CREATE POLICY sensitive_data_access_logs_allow_insert ON sensitive_data_access_logs FOR INSERT USING (true);",
            script);
    }

    [Fact]
    public void MigrationScript_DoesNotDefinePermissiveUpdateOrDeletePolicy()
    {
        // The actual DENY-equivalent: no permissive UPDATE/DELETE policy means those commands are
        // default-denied for every role (including the table owner, under FORCE) once RLS is
        // enabled. Proving the negative directly, not just that a search for "FOR UPDATE"/"FOR
        // DELETE" against this table turns up nothing incidentally.
        // Since ADR-0014 (AddDeletionLog) exactly one DELETE policy exists: the audit-log purge's,
        // limited to the vlms_retention role and to rows older than 6 years — never a permissive one.
        var script = GenerateFullMigrationScript();

        Assert.DoesNotContain("ON sensitive_data_access_logs FOR UPDATE", script);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(script, "ON sensitive_data_access_logs FOR DELETE"));
        Assert.Contains(
            "CREATE POLICY sensitive_data_access_logs_retention_delete ON sensitive_data_access_logs FOR DELETE " +
            "USING (current_user = 'vlms_retention' AND accessed_at < (now() - interval '6 years'));",
            script);
    }

    // ---- ADR-0014: the deletion log and the audit-log purge (AddDeletionLog) ----------------------

    [Fact]
    public void MigrationScript_MakesTheDeletionLogAppendOnly()
    {
        var script = GenerateFullMigrationScript();

        Assert.Contains("REVOKE UPDATE, DELETE ON deletion_log FROM PUBLIC;", script);
        Assert.Contains("REVOKE UPDATE, DELETE ON deletion_log FROM vlms_app;", script);
        Assert.Contains("ALTER TABLE deletion_log ENABLE ROW LEVEL SECURITY;", script);
        Assert.Contains("ALTER TABLE deletion_log FORCE ROW LEVEL SECURITY;", script);
        Assert.Contains("CREATE POLICY deletion_log_allow_select ON deletion_log FOR SELECT USING (true);", script);
        Assert.Contains("CREATE POLICY deletion_log_allow_insert ON deletion_log FOR INSERT WITH CHECK (true);", script);
        Assert.DoesNotContain("ON deletion_log FOR UPDATE", script);
        Assert.DoesNotContain("ON deletion_log FOR DELETE", script);
        Assert.DoesNotContain("ON deletion_log FOR ALL", script);
    }

    [Fact]
    public void MigrationScript_DeletionLogProtection_FollowsItsTable()
    {
        var script = GenerateFullMigrationScript();

        var create = script.IndexOf("CREATE TABLE deletion_log", StringComparison.Ordinal);
        var revoke = script.IndexOf("REVOKE UPDATE, DELETE ON deletion_log FROM PUBLIC;", StringComparison.Ordinal);
        var force = script.IndexOf("ALTER TABLE deletion_log FORCE ROW LEVEL SECURITY;", StringComparison.Ordinal);
        Assert.True(create >= 0);
        Assert.True(revoke > create);
        Assert.True(force > revoke);
    }

    [Fact]
    public void MigrationScript_GrantsNoRoleBypassRls()
    {
        Assert.DoesNotContain("BYPASSRLS", GenerateFullMigrationScript(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MigrationScript_RowLevelSecurityIsAppliedAfterInitialCreateAndRevoke()
    {
        // Must land after both the table exists and the prior REVOKE migration — proving this is a
        // later, additive migration (kept, not folded backwards into either earlier one).
        var script = GenerateFullMigrationScript();

        var createTableIndex = script.IndexOf("CREATE TABLE sensitive_data_access_logs", StringComparison.Ordinal);
        var revokeIndex = script.IndexOf(
            "REVOKE UPDATE, DELETE ON sensitive_data_access_logs FROM PUBLIC;",
            StringComparison.Ordinal);
        var enableIndex = script.IndexOf(
            "ALTER TABLE sensitive_data_access_logs ENABLE ROW LEVEL SECURITY;",
            StringComparison.Ordinal);

        Assert.True(createTableIndex >= 0);
        Assert.True(revokeIndex > createTableIndex);
        Assert.True(enableIndex > revokeIndex, "RLS must be applied after the prior REVOKE migration.");
    }
}
