using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Vlms.Infrastructure.Migrations
{
    /// <summary>
    /// docs/adr/0014-retention-purge.md decisions 4 and 6.
    ///
    /// 1. The deletion log table (<c>DeletionLogEntry</c>), made permanent the way
    ///    <c>sensitive_data_access_logs</c> is (adr/0004 passes 6 and 8): UPDATE and DELETE revoked from
    ///    PUBLIC and from <c>vlms_app</c> (which <c>deploy/postgres/10-grant-app-privileges.sql</c>'s
    ///    ALTER DEFAULT PRIVILEGES would otherwise hand full CRUD on a new table), and row-level
    ///    security ENABLEd and FORCEd with permissive SELECT and INSERT policies only, so UPDATE and
    ///    DELETE are default-denied for every role including the table owner.
    /// 2. The audit-log purge: one DELETE policy on <c>sensitive_data_access_logs</c> admitting only
    ///    rows older than 6 years, and only for the role <c>vlms_retention</c>. Written as a
    ///    <c>current_user</c> test (policy for PUBLIC) rather than <c>TO vlms_retention</c>, because
    ///    <c>TO</c> needs the role to exist when the migration runs and the migration role
    ///    (<c>vlms_owner</c>) cannot create roles. <c>vlms_app</c> still cannot delete: it holds no
    ///    DELETE privilege on the table (privileges are checked before policies) and is not
    ///    <c>vlms_retention</c>. Narrower than BYPASSRLS, which would exempt the role from every policy.
    ///    For a DELETE whose WHERE reads the table, PostgreSQL also applies the SELECT policy
    ///    (USING (true) here), and rows failing the DELETE policy are silently skipped
    ///    (postgresql.org/docs/current/sql-createpolicy.html).
    ///
    /// PostgreSQL-only: the SQL is skipped on any other provider (the tests build their SQLite schema
    /// with EnsureCreated and never run migrations; the guard keeps that true by construction).
    /// </summary>
    public partial class AddDeletionLog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "deletion_log",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    record_type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    record_id = table.Column<int>(type: "integer", nullable: false),
                    deleted_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    deleted_by_user_id = table.Column<int>(type: "integer", nullable: false),
                    chapter_id = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_deletion_log", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_deletion_log_chapter_id_deleted_at",
                table: "deletion_log",
                columns: new[] { "chapter_id", "deleted_at" });

            if (migrationBuilder.ActiveProvider != "Npgsql.EntityFrameworkCore.PostgreSQL")
            {
                return;
            }

            migrationBuilder.Sql("REVOKE UPDATE, DELETE ON deletion_log FROM PUBLIC;");
            migrationBuilder.Sql(
                "DO $$ BEGIN " +
                "IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'vlms_app') THEN " +
                "REVOKE UPDATE, DELETE ON deletion_log FROM vlms_app; " +
                "END IF; END $$;");
            migrationBuilder.Sql("ALTER TABLE deletion_log ENABLE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("ALTER TABLE deletion_log FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("CREATE POLICY deletion_log_allow_select ON deletion_log FOR SELECT USING (true);");
            migrationBuilder.Sql("CREATE POLICY deletion_log_allow_insert ON deletion_log FOR INSERT WITH CHECK (true);");
            // Deliberately no UPDATE or DELETE policy on deletion_log.

            migrationBuilder.Sql(
                "CREATE POLICY sensitive_data_access_logs_retention_delete ON sensitive_data_access_logs FOR DELETE " +
                "USING (current_user = 'vlms_retention' AND accessed_at < (now() - interval '6 years'));");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            if (migrationBuilder.ActiveProvider == "Npgsql.EntityFrameworkCore.PostgreSQL")
            {
                migrationBuilder.Sql("DROP POLICY IF EXISTS sensitive_data_access_logs_retention_delete ON sensitive_data_access_logs;");
            }

            migrationBuilder.DropTable(
                name: "deletion_log");
        }
    }
}
