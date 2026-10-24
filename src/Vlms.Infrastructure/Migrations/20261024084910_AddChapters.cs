using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Vlms.Infrastructure.Migrations
{
    /// <summary>
    /// Chapters (functional.md FR-021, docs/adr/0010-chapters.md). Creates <c>chapters</c> with
    /// Shrewsbury as chapter 1 and <c>active_chapter_choices</c>; adds <c>chapter_id</c> to every
    /// chapter-owned table, filling every existing row with 1 (Shrewsbury) and then dropping that
    /// default; gives <c>user_roles</c> a surrogate key and a nullable <c>chapter_id</c> (1 for every
    /// existing chapter role, null for Approver), unique per (user, role, chapter) and per (user,
    /// role) for national roles. Down drops all of it; it fails to restore the old (user_id, role)
    /// key if anyone has since been given the same role in two chapters, which is the honest result.
    /// </summary>
    public partial class AddChapters : Migration
    {
        private static readonly string[] ChapterOwnedTables =
        [
            "students", "student_rank_progresses", "student_lesson_completions", "student_guardian_links",
            "student_badges", "student_applications", "student_application_sensitive_details", "parent_guardians",
            "guardian_invites", "dbs_checks", "consent_sensitive_details", "consent_records", "certificates"
        ];
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_user_roles",
                table: "user_roles");

            migrationBuilder.AddColumn<int>(
                name: "id",
                table: "user_roles",
                type: "integer",
                nullable: false)
                .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn);

            migrationBuilder.AddColumn<int>(
                name: "chapter_id",
                table: "user_roles",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "chapter_id",
                table: "students",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "chapter_id",
                table: "student_rank_progresses",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "chapter_id",
                table: "student_lesson_completions",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "chapter_id",
                table: "student_guardian_links",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "chapter_id",
                table: "student_badges",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "chapter_id",
                table: "student_applications",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "chapter_id",
                table: "student_application_sensitive_details",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "chapter_id",
                table: "parent_guardians",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "chapter_id",
                table: "guardian_invites",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "chapter_id",
                table: "dbs_checks",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "chapter_id",
                table: "consent_sensitive_details",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "chapter_id",
                table: "consent_records",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "chapter_id",
                table: "certificates",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddPrimaryKey(
                name: "PK_user_roles",
                table: "user_roles",
                column: "id");

            migrationBuilder.CreateTable(
                name: "chapters",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    place = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_chapters", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "active_chapter_choices",
                columns: table => new
                {
                    user_id = table.Column<int>(type: "integer", nullable: false),
                    chapter_id = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_active_chapter_choices", x => x.user_id);
                    table.ForeignKey(
                        name: "FK_active_chapter_choices_app_users_user_id",
                        column: x => x.user_id,
                        principalTable: "app_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_active_chapter_choices_chapters_chapter_id",
                        column: x => x.chapter_id,
                        principalTable: "chapters",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.InsertData(
                table: "chapters",
                columns: new[] { "id", "created_at", "is_active", "name", "place" },
                values: new object[] { 1, new DateTime(2026, 10, 24, 0, 0, 0, 0, DateTimeKind.Utc), true, "Shrewsbury Orthodox Church", "Shrewsbury" });

            // Every role held before chapters is Shrewsbury's, except the national Approver role
            // (Role enum values: Admin 0, Teacher 1, Approver 2, Parent 3, Student 4,
            // SafeguardingOfficer 5). Raw SQL because a data update has no model operation.
            migrationBuilder.Sql("UPDATE user_roles SET chapter_id = 1 WHERE role IN (0, 1, 3, 4, 5);");

            // The default of 1 above only placed the existing rows. Drop it, so a row written later
            // without a chapter fails instead of silently joining Shrewsbury.
            foreach (var table in ChapterOwnedTables)
            {
                migrationBuilder.AlterColumn<int>(
                    name: "chapter_id",
                    table: table,
                    type: "integer",
                    nullable: false,
                    oldClrType: typeof(int),
                    oldType: "integer",
                    oldDefaultValue: 1);
            }

            migrationBuilder.CreateIndex(
                name: "IX_user_roles_chapter_id_role",
                table: "user_roles",
                columns: new[] { "chapter_id", "role" });

            migrationBuilder.CreateIndex(
                name: "IX_user_roles_user_id_role_chapter_id",
                table: "user_roles",
                columns: new[] { "user_id", "role", "chapter_id" },
                unique: true,
                filter: "chapter_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_user_roles_user_id_role_national",
                table: "user_roles",
                columns: new[] { "user_id", "role" },
                unique: true,
                filter: "chapter_id IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_students_chapter_id",
                table: "students",
                column: "chapter_id");

            migrationBuilder.CreateIndex(
                name: "IX_student_rank_progresses_chapter_id",
                table: "student_rank_progresses",
                column: "chapter_id");

            migrationBuilder.CreateIndex(
                name: "IX_student_lesson_completions_chapter_id",
                table: "student_lesson_completions",
                column: "chapter_id");

            migrationBuilder.CreateIndex(
                name: "IX_student_guardian_links_chapter_id",
                table: "student_guardian_links",
                column: "chapter_id");

            migrationBuilder.CreateIndex(
                name: "IX_student_badges_chapter_id",
                table: "student_badges",
                column: "chapter_id");

            migrationBuilder.CreateIndex(
                name: "IX_student_applications_chapter_id",
                table: "student_applications",
                column: "chapter_id");

            migrationBuilder.CreateIndex(
                name: "IX_student_application_sensitive_details_chapter_id",
                table: "student_application_sensitive_details",
                column: "chapter_id");

            migrationBuilder.CreateIndex(
                name: "IX_parent_guardians_chapter_id",
                table: "parent_guardians",
                column: "chapter_id");

            migrationBuilder.CreateIndex(
                name: "IX_guardian_invites_chapter_id",
                table: "guardian_invites",
                column: "chapter_id");

            migrationBuilder.CreateIndex(
                name: "IX_dbs_checks_chapter_id",
                table: "dbs_checks",
                column: "chapter_id");

            migrationBuilder.CreateIndex(
                name: "IX_consent_sensitive_details_chapter_id",
                table: "consent_sensitive_details",
                column: "chapter_id");

            migrationBuilder.CreateIndex(
                name: "IX_consent_records_chapter_id",
                table: "consent_records",
                column: "chapter_id");

            migrationBuilder.CreateIndex(
                name: "IX_certificates_chapter_id",
                table: "certificates",
                column: "chapter_id");

            migrationBuilder.CreateIndex(
                name: "IX_active_chapter_choices_chapter_id",
                table: "active_chapter_choices",
                column: "chapter_id");

            migrationBuilder.CreateIndex(
                name: "IX_chapters_name",
                table: "chapters",
                column: "name",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_certificates_chapters_chapter_id",
                table: "certificates",
                column: "chapter_id",
                principalTable: "chapters",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_consent_records_chapters_chapter_id",
                table: "consent_records",
                column: "chapter_id",
                principalTable: "chapters",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_consent_sensitive_details_chapters_chapter_id",
                table: "consent_sensitive_details",
                column: "chapter_id",
                principalTable: "chapters",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_dbs_checks_chapters_chapter_id",
                table: "dbs_checks",
                column: "chapter_id",
                principalTable: "chapters",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_guardian_invites_chapters_chapter_id",
                table: "guardian_invites",
                column: "chapter_id",
                principalTable: "chapters",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_parent_guardians_chapters_chapter_id",
                table: "parent_guardians",
                column: "chapter_id",
                principalTable: "chapters",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_student_application_sensitive_details_chapters_chapter_id",
                table: "student_application_sensitive_details",
                column: "chapter_id",
                principalTable: "chapters",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_student_applications_chapters_chapter_id",
                table: "student_applications",
                column: "chapter_id",
                principalTable: "chapters",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_student_badges_chapters_chapter_id",
                table: "student_badges",
                column: "chapter_id",
                principalTable: "chapters",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_student_guardian_links_chapters_chapter_id",
                table: "student_guardian_links",
                column: "chapter_id",
                principalTable: "chapters",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_student_lesson_completions_chapters_chapter_id",
                table: "student_lesson_completions",
                column: "chapter_id",
                principalTable: "chapters",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_student_rank_progresses_chapters_chapter_id",
                table: "student_rank_progresses",
                column: "chapter_id",
                principalTable: "chapters",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_students_chapters_chapter_id",
                table: "students",
                column: "chapter_id",
                principalTable: "chapters",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_user_roles_chapters_chapter_id",
                table: "user_roles",
                column: "chapter_id",
                principalTable: "chapters",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_certificates_chapters_chapter_id",
                table: "certificates");

            migrationBuilder.DropForeignKey(
                name: "FK_consent_records_chapters_chapter_id",
                table: "consent_records");

            migrationBuilder.DropForeignKey(
                name: "FK_consent_sensitive_details_chapters_chapter_id",
                table: "consent_sensitive_details");

            migrationBuilder.DropForeignKey(
                name: "FK_dbs_checks_chapters_chapter_id",
                table: "dbs_checks");

            migrationBuilder.DropForeignKey(
                name: "FK_guardian_invites_chapters_chapter_id",
                table: "guardian_invites");

            migrationBuilder.DropForeignKey(
                name: "FK_parent_guardians_chapters_chapter_id",
                table: "parent_guardians");

            migrationBuilder.DropForeignKey(
                name: "FK_student_application_sensitive_details_chapters_chapter_id",
                table: "student_application_sensitive_details");

            migrationBuilder.DropForeignKey(
                name: "FK_student_applications_chapters_chapter_id",
                table: "student_applications");

            migrationBuilder.DropForeignKey(
                name: "FK_student_badges_chapters_chapter_id",
                table: "student_badges");

            migrationBuilder.DropForeignKey(
                name: "FK_student_guardian_links_chapters_chapter_id",
                table: "student_guardian_links");

            migrationBuilder.DropForeignKey(
                name: "FK_student_lesson_completions_chapters_chapter_id",
                table: "student_lesson_completions");

            migrationBuilder.DropForeignKey(
                name: "FK_student_rank_progresses_chapters_chapter_id",
                table: "student_rank_progresses");

            migrationBuilder.DropForeignKey(
                name: "FK_students_chapters_chapter_id",
                table: "students");

            migrationBuilder.DropForeignKey(
                name: "FK_user_roles_chapters_chapter_id",
                table: "user_roles");

            migrationBuilder.DropTable(
                name: "active_chapter_choices");

            migrationBuilder.DropTable(
                name: "chapters");

            migrationBuilder.DropPrimaryKey(
                name: "PK_user_roles",
                table: "user_roles");

            migrationBuilder.DropIndex(
                name: "IX_user_roles_chapter_id_role",
                table: "user_roles");

            migrationBuilder.DropIndex(
                name: "IX_user_roles_user_id_role_chapter_id",
                table: "user_roles");

            migrationBuilder.DropIndex(
                name: "IX_user_roles_user_id_role_national",
                table: "user_roles");

            migrationBuilder.DropIndex(
                name: "IX_students_chapter_id",
                table: "students");

            migrationBuilder.DropIndex(
                name: "IX_student_rank_progresses_chapter_id",
                table: "student_rank_progresses");

            migrationBuilder.DropIndex(
                name: "IX_student_lesson_completions_chapter_id",
                table: "student_lesson_completions");

            migrationBuilder.DropIndex(
                name: "IX_student_guardian_links_chapter_id",
                table: "student_guardian_links");

            migrationBuilder.DropIndex(
                name: "IX_student_badges_chapter_id",
                table: "student_badges");

            migrationBuilder.DropIndex(
                name: "IX_student_applications_chapter_id",
                table: "student_applications");

            migrationBuilder.DropIndex(
                name: "IX_student_application_sensitive_details_chapter_id",
                table: "student_application_sensitive_details");

            migrationBuilder.DropIndex(
                name: "IX_parent_guardians_chapter_id",
                table: "parent_guardians");

            migrationBuilder.DropIndex(
                name: "IX_guardian_invites_chapter_id",
                table: "guardian_invites");

            migrationBuilder.DropIndex(
                name: "IX_dbs_checks_chapter_id",
                table: "dbs_checks");

            migrationBuilder.DropIndex(
                name: "IX_consent_sensitive_details_chapter_id",
                table: "consent_sensitive_details");

            migrationBuilder.DropIndex(
                name: "IX_consent_records_chapter_id",
                table: "consent_records");

            migrationBuilder.DropIndex(
                name: "IX_certificates_chapter_id",
                table: "certificates");

            migrationBuilder.DropColumn(
                name: "id",
                table: "user_roles");

            migrationBuilder.DropColumn(
                name: "chapter_id",
                table: "user_roles");

            migrationBuilder.DropColumn(
                name: "chapter_id",
                table: "students");

            migrationBuilder.DropColumn(
                name: "chapter_id",
                table: "student_rank_progresses");

            migrationBuilder.DropColumn(
                name: "chapter_id",
                table: "student_lesson_completions");

            migrationBuilder.DropColumn(
                name: "chapter_id",
                table: "student_guardian_links");

            migrationBuilder.DropColumn(
                name: "chapter_id",
                table: "student_badges");

            migrationBuilder.DropColumn(
                name: "chapter_id",
                table: "student_applications");

            migrationBuilder.DropColumn(
                name: "chapter_id",
                table: "student_application_sensitive_details");

            migrationBuilder.DropColumn(
                name: "chapter_id",
                table: "parent_guardians");

            migrationBuilder.DropColumn(
                name: "chapter_id",
                table: "guardian_invites");

            migrationBuilder.DropColumn(
                name: "chapter_id",
                table: "dbs_checks");

            migrationBuilder.DropColumn(
                name: "chapter_id",
                table: "consent_sensitive_details");

            migrationBuilder.DropColumn(
                name: "chapter_id",
                table: "consent_records");

            migrationBuilder.DropColumn(
                name: "chapter_id",
                table: "certificates");

            migrationBuilder.AddPrimaryKey(
                name: "PK_user_roles",
                table: "user_roles",
                columns: new[] { "user_id", "role" });
        }
    }
}
