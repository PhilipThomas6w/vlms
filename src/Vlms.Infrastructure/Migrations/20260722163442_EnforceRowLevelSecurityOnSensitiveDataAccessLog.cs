using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vlms.Infrastructure.Migrations
{
    /// <summary>
    /// Pass 8 of adr/0004-sensitive-data-access-control.md §4/§5: closes the gap the previous
    /// migration (<see cref="RevokeUpdateDeleteOnSensitiveDataAccessLog"/>) disclosed but did not
    /// close — that <c>REVOKE UPDATE, DELETE ... FROM PUBLIC</c> has no override effect on
    /// <c>sensitive_data_access_logs</c>'s owner (<c>vlms_owner</c>) or on any role ever directly
    /// <c>GRANT</c>ed <c>UPDATE</c>/<c>DELETE</c> (not via <c>PUBLIC</c>). This migration adds
    /// Row-Level Security, the mechanism that migration's own doc comment and
    /// <c>docs/governance/raid.md</c> D-004 named as the actual DENY-equivalent and deliberately
    /// deferred — this is that follow-up, built with the same higher scrutiny the deferring item
    /// asked for.
    ///
    /// <para>
    /// <b>Verified against current PostgreSQL documentation before writing this
    /// (postgresql.org/docs/current/ddl-rowsecurity.html, sql-altertable.html, sql-createrole.html)
    /// — per CLAUDE.md "verify, don't invent" — rather than assumed from the SQL Server DENY mental
    /// model a second time:</b>
    /// </para>
    /// <list type="bullet">
    /// <item><b><c>ENABLE ROW LEVEL SECURITY</c> vs <c>FORCE ROW LEVEL SECURITY</c> — these are two
    /// independent switches, and both are required here.</b> <c>ENABLE</c> turns row security on for
    /// the table at all: "these forms control the application of row security policies belonging to
    /// the table. If enabled and no policies exist for the table, then a default-deny policy is
    /// applied." But <c>ENABLE</c> alone still exempts the table owner — "table owners normally
    /// bypass row security... though a table owner can choose to be subject to row security with
    /// ALTER TABLE ... FORCE ROW LEVEL SECURITY." <c>FORCE</c> is the second, separate switch that
    /// removes that owner exemption: "these forms control the application of row security policies
    /// belonging to the table when the user is the table owner. If enabled, row-level security
    /// policies will be applied when the user is the table owner." Since <c>vlms_owner</c> (the
    /// table's owner per <c>deploy/postgres/00-create-roles-and-database.sql</c>) is exactly the
    /// role this item exists to additionally constrain — the previous migration's whole finding was
    /// that ownership is untouched by <c>REVOKE</c> — <c>ENABLE</c> without <c>FORCE</c> would leave
    /// the owner fully exempt and solve nothing for the one principal named as the residual risk.
    /// Both statements are issued, in that order (<c>FORCE</c> presupposes row security already being
    /// relevant to the table, and reads more naturally as "and also apply it to the owner" once
    /// <c>ENABLE</c> has established that policies govern the table at all).</item>
    /// <item><b>Default-deny is total once <c>ENABLE</c> is set and a command has no matching
    /// policy — this is what makes "no permissive UPDATE/DELETE policy" equivalent to a DENY, not an
    /// oversight.</b> "If no policy exists for the table, a default-deny policy is used, meaning that
    /// no rows are visible or can be modified" — this applies per-command: a table can have a
    /// permissive <c>SELECT</c> policy and a permissive <c>INSERT</c> policy while <c>UPDATE</c> and
    /// <c>DELETE</c>, having no policy of their own, are separately and fully default-denied. Nothing
    /// needs to say "deny <c>UPDATE</c>/<c>DELETE</c>" explicitly; the absence of a permissive policy
    /// for those two commands, combined with <c>FORCE</c> binding the owner, is the DENY.</item>
    /// <item><b>Who is still exempt, even under <c>FORCE</c> — confirmed as a strictly smaller,
    /// named set, not "anyone with a direct GRANT" the way the prior REVOKE left it:</b> "Superusers
    /// and roles with the <c>BYPASSRLS</c> attribute always bypass the row security system when
    /// accessing a table" — and only those two. <c>BYPASSRLS</c> defaults to off
    /// (<c>NOBYPASSRLS</c> — "NOBYPASSRLS is the default. Only superuser roles or roles with
    /// BYPASSRLS can specify BYPASSRLS") and neither <c>vlms_owner</c> nor <c>vlms_app</c> is ever
    /// created with <c>SUPERUSER</c> or <c>BYPASSRLS</c> in
    /// <c>deploy/postgres/00-create-roles-and-database.sql</c> (both are created
    /// <c>NOSUPERUSER</c> explicitly; neither statement mentions <c>BYPASSRLS</c>, and since it
    /// defaults off and only a superuser/BYPASSRLS-holder could have granted it, it was never
    /// silently acquired). So, for the roles this deployment actually creates, this RLS policy pair
    /// is a true DENY-equivalent for <c>UPDATE</c>/<c>DELETE</c>: the only bypass is a PostgreSQL
    /// superuser session (e.g. the cluster's <c>postgres</c> role, used only for out-of-band
    /// operational work, never as an application connection identity) — the same class of residual
    /// the SQL Server DENY always had via <c>sysadmin</c>, not a new or wider hole.</item>
    /// <item><b><c>USING</c> vs <c>WITH CHECK</c>, and why <c>INSERT</c>'s policy uses only
    /// <c>WITH CHECK</c>:</b> a policy's <c>USING</c> expression filters which existing rows a
    /// command may see/target; its <c>WITH CHECK</c> expression validates rows being written
    /// (inserted, or the post-update image of an updated row). For <c>INSERT</c> specifically there
    /// is no "existing row" to filter — the row does not exist until the statement creates it — so
    /// only <c>WITH CHECK</c> is meaningful and enforced; a <c>FOR INSERT</c> policy that specified
    /// <c>USING</c> instead would be the wrong clause for the command. This migration's
    /// <c>FOR INSERT WITH CHECK (true)</c> is deliberate, not a simplification of "USING (true)" —
    /// getting this backwards was exactly the risk this item's own instructions flagged, and is why
    /// <c>FOR SELECT</c> below uses <c>USING (true)</c> (there being no rows written to check) while
    /// <c>FOR INSERT</c> uses <c>WITH CHECK (true)</c> and nothing else.</item>
    /// </list>
    /// <para>
    /// <b>What this migration does, precisely — and why the two permissive policies are safe for the
    /// single most safety-critical write in this codebase.</b> <c>SensitiveDataAuditInterceptor</c>
    /// (<c>src/Vlms.Infrastructure/Auditing/</c>) writes one <c>SensitiveDataAccessLog</c> row per
    /// <c>DbsCheck</c>/<c>ConsentSensitiveDetails</c> instance materialized, via a plain
    /// <c>DbSet.Add</c> + <c>SaveChanges</c> — an ordinary <c>INSERT</c>, with no row-specific
    /// predicate of its own to satisfy (it is not, for example, scoped to "rows this connection
    /// already owns"). A <c>FOR INSERT WITH CHECK (true)</c> policy admits every such <c>INSERT</c>
    /// unconditionally for any role RLS actually applies to — <c>vlms_app</c> (the interceptor's own
    /// runtime identity) and <c>vlms_owner</c> under <c>FORCE</c> alike — so this cannot silently
    /// block the interceptor's write the way an under-scoped <c>WITH CHECK</c> predicate could have.
    /// The mirrored <c>FOR SELECT USING (true)</c> policy keeps every ordinary read of this table
    /// (the audit trail being read back, e.g. by a future retention/reporting feature) working
    /// unchanged — RLS adds no new row-visibility restriction here, since this control's whole
    /// purpose is tamper protection on <c>UPDATE</c>/<c>DELETE</c>, not read-scoping (the query
    /// filters on <c>DbsCheck</c>/<c>ConsentSensitiveDetails</c> already own read-scoping for the
    /// entities that need it; <c>sensitive_data_access_logs</c> itself was never row-restricted for
    /// reads and this migration does not start restricting it now). No policy exists for
    /// <c>UPDATE</c> or <c>DELETE</c> — by design, per the default-deny behaviour verified above —
    /// so both are now blocked for every role except a genuine PostgreSQL superuser, including
    /// <c>vlms_owner</c> despite owning the table.
    /// </para>
    /// <para>
    /// <b>Kept, not replaced:</b> the prior migration's
    /// <c>REVOKE UPDATE, DELETE ON sensitive_data_access_logs FROM PUBLIC</c> is left exactly as it
    /// was (this migration does not touch it, and is a separate, later-applied migration, per this
    /// project's convention of never rewriting a committed migration). It is genuine defense in
    /// depth, not redundant with RLS: <c>REVOKE</c> operates on PostgreSQL's privilege-grant system
    /// (can this role execute an <c>UPDATE</c>/<c>DELETE</c> statement against this table at all),
    /// while <c>FORCE ROW LEVEL SECURITY</c> + no permissive policy operates one layer further in,
    /// on which specific rows a permitted statement is allowed to touch (verified as functionally
    /// distinct in postgresql.org/docs/current/ddl-rowsecurity.html's own worked examples, which
    /// combine both mechanisms rather than treating either as sufficient alone). A future accidental
    /// <c>GRANT UPDATE ... TO PUBLIC</c> would undo the REVOKE's protection silently, exactly as
    /// pass 6 disclosed — but RLS's default-deny would still block the resulting
    /// <c>UPDATE</c>/<c>DELETE</c> regardless, because RLS is evaluated independently of whether the
    /// statement is otherwise privileged to run.
    /// </para>
    /// <para>
    /// <b>Honest limits of this migration, stated rather than implied away (the same discipline the
    /// prior migration's doc comment applied to its own REVOKE):</b> a genuine PostgreSQL superuser
    /// session still bypasses this entirely — that residual is disclosed, not solved, the same way
    /// SQL Server's original DENY always left <c>sysadmin</c> as an unavoidable exemption. This
    /// migration also cannot be exercised against the SQLite in-memory provider this test suite uses
    /// for everything else (SQLite has no row-security model; tests build schema via
    /// <c>Database.EnsureCreated()</c>, never <c>Database.Migrate()</c>) — verified instead via
    /// <c>tests/Vlms.Tests/Infrastructure/MigrationsTests.cs</c>'s <c>IMigrator.GenerateScript()</c>
    /// pattern (same approach as the prior migration), which proves the exact SQL text, statement
    /// ordering, and USING/WITH CHECK clause placement are correct — not that PostgreSQL executes
    /// them with the documented semantics against a live server. No Testcontainers-backed or other
    /// live-PostgreSQL integration test exists in this repository (considered and explicitly not
    /// built for the prior migration too, per <c>openwiki/testing.md</c>'s "Test-provider strategy"
    /// section — no Docker daemon is available in this project's build/dev environment either, which
    /// was checked, not assumed, before reaching the same conclusion again) — this remains
    /// "structurally correct, verified against current PostgreSQL documentation, not yet exercised
    /// against a live PostgreSQL server," the same disclosed status the prior migration and
    /// <c>AzureBlobStorage</c>/<c>EntraCurrentUserContext</c> carried before their live dependencies
    /// existed. A manual verification procedure for whoever first runs this migration against a real
    /// server is documented in <c>docs/adr/0004-sensitive-data-access-control.md</c> §4 pass 8 and
    /// <c>openwiki/access-control.md</c>.
    /// </para>
    /// </summary>
    public partial class EnforceRowLevelSecurityOnSensitiveDataAccessLog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Order matters: ENABLE first (turns row security on for the table at all), then FORCE
            // (removes the table owner's default exemption from it) — see class doc comment.
            migrationBuilder.Sql(
                "ALTER TABLE sensitive_data_access_logs ENABLE ROW LEVEL SECURITY;");
            migrationBuilder.Sql(
                "ALTER TABLE sensitive_data_access_logs FORCE ROW LEVEL SECURITY;");

            // Permissive SELECT: RLS adds no new read-scoping to this table (it never had any) —
            // this policy exists purely so ordinary reads keep working once RLS is enabled, since
            // enabling RLS with zero policies default-denies every command, including SELECT.
            migrationBuilder.Sql(
                "CREATE POLICY sensitive_data_access_logs_allow_select " +
                "ON sensitive_data_access_logs FOR SELECT USING (true);");

            // Permissive INSERT: keeps SensitiveDataAuditInterceptor's own append-only writes
            // working. WITH CHECK, not USING — INSERT has no existing row to filter, only a new row
            // to validate (see class doc comment's USING-vs-WITH-CHECK explanation).
            migrationBuilder.Sql(
                "CREATE POLICY sensitive_data_access_logs_allow_insert " +
                "ON sensitive_data_access_logs FOR INSERT WITH CHECK (true);");

            // Deliberately no UPDATE/DELETE policy. Under FORCE ROW LEVEL SECURITY, the absence of a
            // matching policy default-denies the command entirely, for every role including the
            // table owner — the actual DENY-equivalent this migration exists to add.
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "DROP POLICY IF EXISTS sensitive_data_access_logs_allow_insert ON sensitive_data_access_logs;");
            migrationBuilder.Sql(
                "DROP POLICY IF EXISTS sensitive_data_access_logs_allow_select ON sensitive_data_access_logs;");
            migrationBuilder.Sql(
                "ALTER TABLE sensitive_data_access_logs NO FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql(
                "ALTER TABLE sensitive_data_access_logs DISABLE ROW LEVEL SECURITY;");
        }
    }
}
