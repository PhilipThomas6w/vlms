using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vlms.Infrastructure.Migrations
{
    /// <summary>
    /// adr/0004-sensitive-data-access-control.md §4's database-level tamper protection, ported for
    /// PostgreSQL per adr/0005-hosting-pivot-self-hosted-vps-postgresql.md (which replaces the SQL
    /// Server engine this mechanism was originally built against — see the two now-deleted
    /// migrations <c>DenyUpdateDeleteOnSensitiveDataAccessLogs</c> /
    /// <c>SupersedeVlmsAppRoleWithPublicDenyOnSensitiveDataAccessLogs</c>, whose intent and full
    /// history remain recorded in ADR-0004 §4/Alternatives — this migration reproduces their net
    /// effect in one step for the new provider, per this item's own instruction not to replay the
    /// two-step SQL-Server-only history).
    ///
    /// <para>
    /// <b>The SQL Server DENY -&gt; PostgreSQL REVOKE port is NOT semantically equivalent, and that
    /// gap is disclosed here rather than papered over</b> — checked against current PostgreSQL
    /// documentation (postgresql.org/docs/current/sql-revoke.html,
    /// postgresql.org/docs/current/ddl-priv.html), not assumed from the SQL Server mental model, per
    /// CLAUDE.md "verify, don't invent":
    /// </para>
    /// <list type="bullet">
    /// <item>SQL Server's <c>DENY</c> is an override: "DENY takes precedence over all permissions,"
    /// so <c>DENY ... TO public</c> blocked every principal regardless of any other GRANT that
    /// principal held, present or future — the only exemptions were the object owner and
    /// <c>sysadmin</c>.</item>
    /// <item>PostgreSQL's privilege model has no override concept. <c>REVOKE ... FROM PUBLIC</c>
    /// removes only the privilege a role held <em>via the PUBLIC pseudo-role</em>; a role that holds
    /// <c>UPDATE</c>/<c>DELETE</c> some other way — an explicit direct <c>GRANT</c>, or by owning the
    /// table — is completely unaffected ("revoking SELECT privilege from PUBLIC does not necessarily
    /// mean that all roles have lost SELECT privilege on the object: those who have it granted
    /// directly ... will still have it" — postgresql.org/docs/current/sql-revoke.html). It is also
    /// not persistent the way DENY is: a later <c>GRANT ... TO PUBLIC</c> would silently restore the
    /// privilege this REVOKE removes, with nothing re-blocking it.</item>
    /// <item>PostgreSQL also grants <b>no default privileges to PUBLIC on tables at all</b>
    /// ("No privileges are granted to PUBLIC by default on tables ..." —
    /// postgresql.org/docs/current/ddl-priv.html), unlike functions/languages. So on a freshly
    /// created table, this REVOKE has nothing to revoke — it is a defensive/documentary statement
    /// against a future accidental <c>GRANT ... TO PUBLIC</c>, not an active restriction on day one.</item>
    /// </list>
    /// <para>
    /// <b>What this means in practice — who can still UPDATE/DELETE this table:</b> not just "the
    /// table owner and a superuser" (the analogy this item was drafted expecting, mirroring SQL
    /// Server's <c>dbo</c>/<c>sysadmin</c>) but <em>any role ever explicitly granted
    /// <c>UPDATE</c>/<c>DELETE</c> on this table directly, or that owns it</em> — a strictly larger
    /// set than DENY's exemption list. The load-bearing control is therefore, and remains, entirely
    /// operational: the application's connecting role must never be this table's owner and must
    /// never be granted <c>UPDATE</c>/<c>DELETE</c> on it directly (only <c>SELECT</c>/<c>INSERT</c>,
    /// which <see cref="Vlms.Infrastructure.Auditing.SensitiveDataAuditInterceptor"/>'s own writes
    /// need) — already tracked as the least-privilege connection requirement in
    /// <c>governance/raid.md</c> D-004, now carrying more real weight than it did under SQL Server's
    /// DENY, where a stray direct GRANT to the app role would still have been blocked.
    /// </para>
    /// <para>
    /// <b>A true DENY-equivalent — one that blocks UPDATE/DELETE for every role including the table
    /// owner, overriding any GRANT, with only a superuser exempt — does exist in PostgreSQL: Row-Level
    /// Security</b> (<c>ALTER TABLE ... ENABLE ROW LEVEL SECURITY</c> +
    /// <c>FORCE ROW LEVEL SECURITY</c> so the policy also binds the owner, plus
    /// <c>CREATE POLICY ... FOR UPDATE USING (false)</c> / <c>FOR DELETE USING (false)</c> — verified
    /// against postgresql.org/docs/current/ddl-rowsecurity.html: "Superusers and roles with the
    /// BYPASSRLS attribute always bypass row security... Table owners normally bypass row security
    /// as well, though a table owner can choose to be subject to row security with
    /// ALTER TABLE ... FORCE ROW LEVEL SECURITY"). This was deliberately <b>not</b> built in this
    /// migration: enabling row security on a table requires an explicit policy for every command
    /// type that must keep working, or that command is default-denied too ("if row-level security is
    /// enabled for a table, but no applicable policies exist, a 'default deny' policy is assumed" —
    /// same page) — so getting this wrong would silently block the audit interceptor's own
    /// <c>INSERT</c> path (the single most safety-critical write in this codebase) or the table's own
    /// reads, inside what this ADR/item scoped as a same-provider *port*, not a mechanism redesign.
    /// Tracked as a new, separate, deliberately higher-scrutiny follow-up item (STATE.md Next) rather
    /// than attempted here.
    /// </para>
    /// <para>
    /// <c>INSERT</c> is still not revoked (the audit interceptor's own writes must keep working) and
    /// <c>TRUNCATE</c> is not revoked either (named nowhere in the ADR or
    /// <c>governance/security-compliance.md</c>) — unchanged from the SQL Server version. Verified via
    /// <c>tests/Vlms.Tests/Infrastructure/MigrationsTests.cs</c>, which generates the real migration
    /// SQL in-process through EF Core's <c>IMigrator.GenerateScript()</c> (now against the
    /// Npgsql-configured context) and asserts the exact <c>REVOKE</c> statement, and that
    /// <c>INSERT</c>/<c>TRUNCATE</c> are not touched — this cannot be exercised against the SQLite
    /// in-memory provider the rest of the test suite uses (tests build schema via
    /// <c>Database.EnsureCreated()</c>, never <c>Database.Migrate()</c>), the same "structurally
    /// correct, not yet exercised against a live resource" status other provider-specific DDL in this
    /// codebase has carried before a live target existed.
    /// </para>
    /// </summary>
    public partial class RevokeUpdateDeleteOnSensitiveDataAccessLog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Defensive/documentary — see class doc comment for exactly what this does and does not
            // guarantee on PostgreSQL. REVOKE is safe to re-issue (no error if PUBLIC never held the
            // privilege in the first place).
            migrationBuilder.Sql(
                "REVOKE UPDATE, DELETE ON sensitive_data_access_logs FROM PUBLIC;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "GRANT UPDATE, DELETE ON sensitive_data_access_logs TO PUBLIC;");
        }
    }
}
