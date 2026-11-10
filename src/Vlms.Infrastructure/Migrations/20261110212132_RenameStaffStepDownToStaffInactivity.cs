using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vlms.Infrastructure.Migrations
{
    /// <summary>
    /// Naming only (FR-017 calls the state Inactive / reactivated): <c>staff_step_downs</c> becomes
    /// <c>staff_inactivities</c> and its columns, indexes and constraints follow. Renames only, so no
    /// row is dropped, copied or rewritten; Down reverses each rename.
    /// </summary>
    public partial class RenameStaffStepDownToStaffInactivity : Migration
    {
        private static readonly (string Old, string New)[] Columns =
        [
            ("stepped_down_at", "inactive_at"),
            ("stepped_down_by_user_id", "marked_inactive_by_user_id"),
            ("reinstated_at", "reactivated_at"),
            ("reinstated_by_user_id", "reactivated_by_user_id"),
        ];

        // Indexes (the primary key's index carries the constraint name, so renaming it renames the key).
        private static readonly (string Old, string New)[] Indexes =
        [
            ("PK_staff_step_downs", "PK_staff_inactivities"),
            ("IX_staff_step_downs_chapter_id", "IX_staff_inactivities_chapter_id"),
            ("IX_staff_step_downs_reinstated_by_user_id", "IX_staff_inactivities_reactivated_by_user_id"),
            ("IX_staff_step_downs_stepped_down_by_user_id", "IX_staff_inactivities_marked_inactive_by_user_id"),
            ("IX_staff_step_downs_user_id_stepped_down_at", "IX_staff_inactivities_user_id_inactive_at"),
        ];

        private static readonly (string Old, string New)[] ForeignKeys =
        [
            ("FK_staff_step_downs_app_users_reinstated_by_user_id", "FK_staff_inactivities_app_users_reactivated_by_user_id"),
            ("FK_staff_step_downs_app_users_stepped_down_by_user_id", "FK_staff_inactivities_app_users_marked_inactive_by_user_id"),
            ("FK_staff_step_downs_app_users_user_id", "FK_staff_inactivities_app_users_user_id"),
            ("FK_staff_step_downs_chapters_chapter_id", "FK_staff_inactivities_chapters_chapter_id"),
        ];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            foreach (var (oldName, newName) in Columns)
                migrationBuilder.RenameColumn(name: oldName, table: "staff_step_downs", newName: newName);

            migrationBuilder.RenameTable(name: "staff_step_downs", newName: "staff_inactivities");

            foreach (var (oldName, newName) in Indexes)
                migrationBuilder.RenameIndex(name: oldName, table: "staff_inactivities", newName: newName);

            // EF has no foreign-key rename operation.
            foreach (var (oldName, newName) in ForeignKeys)
                migrationBuilder.Sql($"ALTER TABLE staff_inactivities RENAME CONSTRAINT \"{oldName}\" TO \"{newName}\";");

            migrationBuilder.Sql("ALTER SEQUENCE IF EXISTS staff_step_downs_id_seq RENAME TO staff_inactivities_id_seq;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("ALTER SEQUENCE IF EXISTS staff_inactivities_id_seq RENAME TO staff_step_downs_id_seq;");

            foreach (var (oldName, newName) in ForeignKeys)
                migrationBuilder.Sql($"ALTER TABLE staff_inactivities RENAME CONSTRAINT \"{newName}\" TO \"{oldName}\";");

            foreach (var (oldName, newName) in Indexes)
                migrationBuilder.RenameIndex(name: newName, table: "staff_inactivities", newName: oldName);

            migrationBuilder.RenameTable(name: "staff_inactivities", newName: "staff_step_downs");

            foreach (var (oldName, newName) in Columns)
                migrationBuilder.RenameColumn(name: newName, table: "staff_step_downs", newName: oldName);
        }
    }
}
