using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vlms.Infrastructure.Migrations
{
    /// <summary>
    /// docs/adr/0014-retention-purge.md, settled after build (Philip, 27/10/2026): a record that names a person only as
    /// the one who acted (a register taken by, a lesson version suggested, approved or published by, a step-down
    /// recorded or reinstated by, a guardian link or invite created by, and the like) no longer keeps that person's
    /// account. Each such foreign key to app_users becomes nullable with ON DELETE SET NULL, so the database clears
    /// the link inside the deletion's own transaction and the record keeps its content; the pages then show "a
    /// former member of staff". The foreign keys that are about the person (their roles, guardian row, student
    /// record, DBS checks, applications, step-down rows) are unchanged.
    ///
    /// Two flags tell a cleared reference apart from one that was legitimately empty:
    /// staff_step_downs.recorded_before_history (a step-down copied in with no recorder) and
    /// lesson_versions.published_directly (which of the approver and publisher a version named). Both are filled
    /// below from the data as it is now, while no reference can yet have been cleared.
    ///
    /// The append-only tables are not touched: sensitive_data_access_logs.user_id and deletion_log.deleted_by_user_id
    /// are plain values with no foreign key, so nothing here needs an UPDATE their row-level security forbids, and
    /// the vlms_retention role and policies (20261027144426_AddDeletionLog) are unchanged.
    ///
    /// Down restores NOT NULL on the formerly required columns, which fails once any account they named has been
    /// deleted; that is the intended refusal, as the person can no longer be put back.
    /// </summary>
    public partial class ClearDeletedAccountReferences : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_attendance_registers_app_users_taken_by_user_id",
                table: "attendance_registers");

            migrationBuilder.DropForeignKey(
                name: "FK_chapter_events_app_users_created_by_user_id",
                table: "chapter_events");

            migrationBuilder.DropForeignKey(
                name: "FK_consent_records_app_users_approved_by_user_id",
                table: "consent_records");

            migrationBuilder.DropForeignKey(
                name: "FK_consent_records_app_users_submitted_by_user_id",
                table: "consent_records");

            migrationBuilder.DropForeignKey(
                name: "FK_guardian_invites_app_users_consumed_by_app_user_id",
                table: "guardian_invites");

            migrationBuilder.DropForeignKey(
                name: "FK_guardian_invites_app_users_created_by_user_id",
                table: "guardian_invites");

            migrationBuilder.DropForeignKey(
                name: "FK_lesson_publishing_setting_changes_app_users_changed_by_user~",
                table: "lesson_publishing_setting_changes");

            migrationBuilder.DropForeignKey(
                name: "FK_lesson_suggestion_approvals_app_users_approver_user_id",
                table: "lesson_suggestion_approvals");

            migrationBuilder.DropForeignKey(
                name: "FK_lesson_suggestions_app_users_author_user_id",
                table: "lesson_suggestions");

            migrationBuilder.DropForeignKey(
                name: "FK_lesson_suggestions_app_users_decided_by_user_id",
                table: "lesson_suggestions");

            migrationBuilder.DropForeignKey(
                name: "FK_lesson_suggestions_app_users_referred_by_user_id",
                table: "lesson_suggestions");

            migrationBuilder.DropForeignKey(
                name: "FK_lesson_versions_app_users_approved_by_user_id",
                table: "lesson_versions");

            migrationBuilder.DropForeignKey(
                name: "FK_lesson_versions_app_users_published_by_user_id",
                table: "lesson_versions");

            migrationBuilder.DropForeignKey(
                name: "FK_lesson_versions_app_users_suggested_by_user_id",
                table: "lesson_versions");

            migrationBuilder.DropForeignKey(
                name: "FK_staff_step_downs_app_users_reinstated_by_user_id",
                table: "staff_step_downs");

            migrationBuilder.DropForeignKey(
                name: "FK_staff_step_downs_app_users_stepped_down_by_user_id",
                table: "staff_step_downs");

            migrationBuilder.DropForeignKey(
                name: "FK_student_applications_app_users_admin_decided_by_user_id",
                table: "student_applications");

            migrationBuilder.DropForeignKey(
                name: "FK_student_applications_app_users_safeguarding_reviewed_by_use~",
                table: "student_applications");

            migrationBuilder.DropForeignKey(
                name: "FK_student_guardian_links_app_users_created_by_user_id",
                table: "student_guardian_links");

            migrationBuilder.DropForeignKey(
                name: "FK_student_lesson_completions_app_users_completed_by_user_id",
                table: "student_lesson_completions");

            migrationBuilder.DropForeignKey(
                name: "FK_students_app_users_assigned_teacher_user_id",
                table: "students");

            migrationBuilder.AlterColumn<int>(
                name: "completed_by_user_id",
                table: "student_lesson_completions",
                type: "integer",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.AlterColumn<int>(
                name: "created_by_user_id",
                table: "student_guardian_links",
                type: "integer",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.AddColumn<bool>(
                name: "recorded_before_history",
                table: "staff_step_downs",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "published_directly",
                table: "lesson_versions",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // Until now every one of these references was ON DELETE RESTRICT, so no account they name has been
            // deleted: a step-down with no recorder is one copied in when staff_step_downs was created, and a version
            // published directly is exactly one with a publisher. Recorded now, before any reference can be cleared.
            migrationBuilder.Sql(
                "UPDATE staff_step_downs SET recorded_before_history = TRUE WHERE stepped_down_by_user_id IS NULL;");
            migrationBuilder.Sql(
                "UPDATE lesson_versions SET published_directly = TRUE WHERE published_by_user_id IS NOT NULL;");

            migrationBuilder.AlterColumn<int>(
                name: "author_user_id",
                table: "lesson_suggestions",
                type: "integer",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.AlterColumn<int>(
                name: "approver_user_id",
                table: "lesson_suggestion_approvals",
                type: "integer",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.AlterColumn<int>(
                name: "changed_by_user_id",
                table: "lesson_publishing_setting_changes",
                type: "integer",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.AlterColumn<int>(
                name: "created_by_user_id",
                table: "guardian_invites",
                type: "integer",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.AlterColumn<int>(
                name: "created_by_user_id",
                table: "chapter_events",
                type: "integer",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.AlterColumn<int>(
                name: "taken_by_user_id",
                table: "attendance_registers",
                type: "integer",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.AddForeignKey(
                name: "FK_attendance_registers_app_users_taken_by_user_id",
                table: "attendance_registers",
                column: "taken_by_user_id",
                principalTable: "app_users",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_chapter_events_app_users_created_by_user_id",
                table: "chapter_events",
                column: "created_by_user_id",
                principalTable: "app_users",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_consent_records_app_users_approved_by_user_id",
                table: "consent_records",
                column: "approved_by_user_id",
                principalTable: "app_users",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_consent_records_app_users_submitted_by_user_id",
                table: "consent_records",
                column: "submitted_by_user_id",
                principalTable: "app_users",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_guardian_invites_app_users_consumed_by_app_user_id",
                table: "guardian_invites",
                column: "consumed_by_app_user_id",
                principalTable: "app_users",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_guardian_invites_app_users_created_by_user_id",
                table: "guardian_invites",
                column: "created_by_user_id",
                principalTable: "app_users",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_lesson_publishing_setting_changes_app_users_changed_by_user~",
                table: "lesson_publishing_setting_changes",
                column: "changed_by_user_id",
                principalTable: "app_users",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_lesson_suggestion_approvals_app_users_approver_user_id",
                table: "lesson_suggestion_approvals",
                column: "approver_user_id",
                principalTable: "app_users",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_lesson_suggestions_app_users_author_user_id",
                table: "lesson_suggestions",
                column: "author_user_id",
                principalTable: "app_users",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_lesson_suggestions_app_users_decided_by_user_id",
                table: "lesson_suggestions",
                column: "decided_by_user_id",
                principalTable: "app_users",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_lesson_suggestions_app_users_referred_by_user_id",
                table: "lesson_suggestions",
                column: "referred_by_user_id",
                principalTable: "app_users",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_lesson_versions_app_users_approved_by_user_id",
                table: "lesson_versions",
                column: "approved_by_user_id",
                principalTable: "app_users",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_lesson_versions_app_users_published_by_user_id",
                table: "lesson_versions",
                column: "published_by_user_id",
                principalTable: "app_users",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_lesson_versions_app_users_suggested_by_user_id",
                table: "lesson_versions",
                column: "suggested_by_user_id",
                principalTable: "app_users",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_staff_step_downs_app_users_reinstated_by_user_id",
                table: "staff_step_downs",
                column: "reinstated_by_user_id",
                principalTable: "app_users",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_staff_step_downs_app_users_stepped_down_by_user_id",
                table: "staff_step_downs",
                column: "stepped_down_by_user_id",
                principalTable: "app_users",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_student_applications_app_users_admin_decided_by_user_id",
                table: "student_applications",
                column: "admin_decided_by_user_id",
                principalTable: "app_users",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_student_applications_app_users_safeguarding_reviewed_by_use~",
                table: "student_applications",
                column: "safeguarding_reviewed_by_user_id",
                principalTable: "app_users",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_student_guardian_links_app_users_created_by_user_id",
                table: "student_guardian_links",
                column: "created_by_user_id",
                principalTable: "app_users",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_student_lesson_completions_app_users_completed_by_user_id",
                table: "student_lesson_completions",
                column: "completed_by_user_id",
                principalTable: "app_users",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_students_app_users_assigned_teacher_user_id",
                table: "students",
                column: "assigned_teacher_user_id",
                principalTable: "app_users",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_attendance_registers_app_users_taken_by_user_id",
                table: "attendance_registers");

            migrationBuilder.DropForeignKey(
                name: "FK_chapter_events_app_users_created_by_user_id",
                table: "chapter_events");

            migrationBuilder.DropForeignKey(
                name: "FK_consent_records_app_users_approved_by_user_id",
                table: "consent_records");

            migrationBuilder.DropForeignKey(
                name: "FK_consent_records_app_users_submitted_by_user_id",
                table: "consent_records");

            migrationBuilder.DropForeignKey(
                name: "FK_guardian_invites_app_users_consumed_by_app_user_id",
                table: "guardian_invites");

            migrationBuilder.DropForeignKey(
                name: "FK_guardian_invites_app_users_created_by_user_id",
                table: "guardian_invites");

            migrationBuilder.DropForeignKey(
                name: "FK_lesson_publishing_setting_changes_app_users_changed_by_user~",
                table: "lesson_publishing_setting_changes");

            migrationBuilder.DropForeignKey(
                name: "FK_lesson_suggestion_approvals_app_users_approver_user_id",
                table: "lesson_suggestion_approvals");

            migrationBuilder.DropForeignKey(
                name: "FK_lesson_suggestions_app_users_author_user_id",
                table: "lesson_suggestions");

            migrationBuilder.DropForeignKey(
                name: "FK_lesson_suggestions_app_users_decided_by_user_id",
                table: "lesson_suggestions");

            migrationBuilder.DropForeignKey(
                name: "FK_lesson_suggestions_app_users_referred_by_user_id",
                table: "lesson_suggestions");

            migrationBuilder.DropForeignKey(
                name: "FK_lesson_versions_app_users_approved_by_user_id",
                table: "lesson_versions");

            migrationBuilder.DropForeignKey(
                name: "FK_lesson_versions_app_users_published_by_user_id",
                table: "lesson_versions");

            migrationBuilder.DropForeignKey(
                name: "FK_lesson_versions_app_users_suggested_by_user_id",
                table: "lesson_versions");

            migrationBuilder.DropForeignKey(
                name: "FK_staff_step_downs_app_users_reinstated_by_user_id",
                table: "staff_step_downs");

            migrationBuilder.DropForeignKey(
                name: "FK_staff_step_downs_app_users_stepped_down_by_user_id",
                table: "staff_step_downs");

            migrationBuilder.DropForeignKey(
                name: "FK_student_applications_app_users_admin_decided_by_user_id",
                table: "student_applications");

            migrationBuilder.DropForeignKey(
                name: "FK_student_applications_app_users_safeguarding_reviewed_by_use~",
                table: "student_applications");

            migrationBuilder.DropForeignKey(
                name: "FK_student_guardian_links_app_users_created_by_user_id",
                table: "student_guardian_links");

            migrationBuilder.DropForeignKey(
                name: "FK_student_lesson_completions_app_users_completed_by_user_id",
                table: "student_lesson_completions");

            migrationBuilder.DropForeignKey(
                name: "FK_students_app_users_assigned_teacher_user_id",
                table: "students");

            migrationBuilder.DropColumn(
                name: "recorded_before_history",
                table: "staff_step_downs");

            migrationBuilder.DropColumn(
                name: "published_directly",
                table: "lesson_versions");

            migrationBuilder.AlterColumn<int>(
                name: "completed_by_user_id",
                table: "student_lesson_completions",
                type: "integer",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);

            migrationBuilder.AlterColumn<int>(
                name: "created_by_user_id",
                table: "student_guardian_links",
                type: "integer",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);

            migrationBuilder.AlterColumn<int>(
                name: "author_user_id",
                table: "lesson_suggestions",
                type: "integer",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);

            migrationBuilder.AlterColumn<int>(
                name: "approver_user_id",
                table: "lesson_suggestion_approvals",
                type: "integer",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);

            migrationBuilder.AlterColumn<int>(
                name: "changed_by_user_id",
                table: "lesson_publishing_setting_changes",
                type: "integer",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);

            migrationBuilder.AlterColumn<int>(
                name: "created_by_user_id",
                table: "guardian_invites",
                type: "integer",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);

            migrationBuilder.AlterColumn<int>(
                name: "created_by_user_id",
                table: "chapter_events",
                type: "integer",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);

            migrationBuilder.AlterColumn<int>(
                name: "taken_by_user_id",
                table: "attendance_registers",
                type: "integer",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);

            migrationBuilder.AddForeignKey(
                name: "FK_attendance_registers_app_users_taken_by_user_id",
                table: "attendance_registers",
                column: "taken_by_user_id",
                principalTable: "app_users",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_chapter_events_app_users_created_by_user_id",
                table: "chapter_events",
                column: "created_by_user_id",
                principalTable: "app_users",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_consent_records_app_users_approved_by_user_id",
                table: "consent_records",
                column: "approved_by_user_id",
                principalTable: "app_users",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_consent_records_app_users_submitted_by_user_id",
                table: "consent_records",
                column: "submitted_by_user_id",
                principalTable: "app_users",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_guardian_invites_app_users_consumed_by_app_user_id",
                table: "guardian_invites",
                column: "consumed_by_app_user_id",
                principalTable: "app_users",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_guardian_invites_app_users_created_by_user_id",
                table: "guardian_invites",
                column: "created_by_user_id",
                principalTable: "app_users",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_lesson_publishing_setting_changes_app_users_changed_by_user~",
                table: "lesson_publishing_setting_changes",
                column: "changed_by_user_id",
                principalTable: "app_users",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_lesson_suggestion_approvals_app_users_approver_user_id",
                table: "lesson_suggestion_approvals",
                column: "approver_user_id",
                principalTable: "app_users",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_lesson_suggestions_app_users_author_user_id",
                table: "lesson_suggestions",
                column: "author_user_id",
                principalTable: "app_users",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_lesson_suggestions_app_users_decided_by_user_id",
                table: "lesson_suggestions",
                column: "decided_by_user_id",
                principalTable: "app_users",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_lesson_suggestions_app_users_referred_by_user_id",
                table: "lesson_suggestions",
                column: "referred_by_user_id",
                principalTable: "app_users",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_lesson_versions_app_users_approved_by_user_id",
                table: "lesson_versions",
                column: "approved_by_user_id",
                principalTable: "app_users",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_lesson_versions_app_users_published_by_user_id",
                table: "lesson_versions",
                column: "published_by_user_id",
                principalTable: "app_users",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_lesson_versions_app_users_suggested_by_user_id",
                table: "lesson_versions",
                column: "suggested_by_user_id",
                principalTable: "app_users",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_staff_step_downs_app_users_reinstated_by_user_id",
                table: "staff_step_downs",
                column: "reinstated_by_user_id",
                principalTable: "app_users",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_staff_step_downs_app_users_stepped_down_by_user_id",
                table: "staff_step_downs",
                column: "stepped_down_by_user_id",
                principalTable: "app_users",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_student_applications_app_users_admin_decided_by_user_id",
                table: "student_applications",
                column: "admin_decided_by_user_id",
                principalTable: "app_users",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_student_applications_app_users_safeguarding_reviewed_by_use~",
                table: "student_applications",
                column: "safeguarding_reviewed_by_user_id",
                principalTable: "app_users",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_student_guardian_links_app_users_created_by_user_id",
                table: "student_guardian_links",
                column: "created_by_user_id",
                principalTable: "app_users",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_student_lesson_completions_app_users_completed_by_user_id",
                table: "student_lesson_completions",
                column: "completed_by_user_id",
                principalTable: "app_users",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_students_app_users_assigned_teacher_user_id",
                table: "students",
                column: "assigned_teacher_user_id",
                principalTable: "app_users",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
