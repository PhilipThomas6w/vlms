using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vlms.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddTeachersNotes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_lesson_suggestions_rank_id",
                table: "lesson_suggestions");

            migrationBuilder.AddColumn<string>(
                name: "teachers_notes",
                table: "lessons",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "teachers_notes",
                table: "lesson_versions",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "teachers_notes",
                table: "lesson_suggestions",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "teachers_notes_only",
                table: "lesson_pictures",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "teachers_information_versions",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false),
                    rank_id = table.Column<int>(type: "integer", nullable: false),
                    number = table.Column<int>(type: "integer", nullable: false),
                    kind = table.Column<int>(type: "integer", nullable: false),
                    body = table.Column<string>(type: "text", nullable: true),
                    suggested_by_user_id = table.Column<int>(type: "integer", nullable: true),
                    suggested_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    approved_by_user_id = table.Column<int>(type: "integer", nullable: true),
                    published_by_user_id = table.Column<int>(type: "integer", nullable: true),
                    published_directly = table.Column<bool>(type: "boolean", nullable: false),
                    reason = table.Column<string>(type: "text", nullable: true),
                    published_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    suggestion_id = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_teachers_information_versions", x => x.id);
                    table.ForeignKey(
                        name: "FK_teachers_information_versions_app_users_approved_by_user_id",
                        column: x => x.approved_by_user_id,
                        principalTable: "app_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_teachers_information_versions_app_users_published_by_user_id",
                        column: x => x.published_by_user_id,
                        principalTable: "app_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_teachers_information_versions_app_users_suggested_by_user_id",
                        column: x => x.suggested_by_user_id,
                        principalTable: "app_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_teachers_information_versions_lesson_suggestions_suggestion~",
                        column: x => x.suggestion_id,
                        principalTable: "lesson_suggestions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_teachers_information_versions_ranks_rank_id",
                        column: x => x.rank_id,
                        principalTable: "ranks",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "teachers_information_version_pictures",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false),
                    teachers_information_version_id = table.Column<int>(type: "integer", nullable: false),
                    order = table.Column<int>(type: "integer", nullable: false),
                    file_name = table.Column<string>(type: "text", nullable: false),
                    blob_key = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_teachers_information_version_pictures", x => x.id);
                    table.ForeignKey(
                        name: "FK_teachers_information_version_pictures_teachers_information_~",
                        column: x => x.teachers_information_version_id,
                        principalTable: "teachers_information_versions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_lesson_suggestions_rank_id_teachers_information_open",
                table: "lesson_suggestions",
                columns: new[] { "rank_id", "kind" },
                unique: true,
                filter: "kind = 4 AND status IN (0, 1)");

            migrationBuilder.CreateIndex(
                name: "IX_teachers_information_version_pictures_teachers_information_~",
                table: "teachers_information_version_pictures",
                columns: new[] { "teachers_information_version_id", "file_name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_teachers_information_versions_approved_by_user_id",
                table: "teachers_information_versions",
                column: "approved_by_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_teachers_information_versions_published_by_user_id",
                table: "teachers_information_versions",
                column: "published_by_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_teachers_information_versions_rank_id_number",
                table: "teachers_information_versions",
                columns: new[] { "rank_id", "number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_teachers_information_versions_suggested_by_user_id",
                table: "teachers_information_versions",
                column: "suggested_by_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_teachers_information_versions_suggestion_id",
                table: "teachers_information_versions",
                column: "suggestion_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "teachers_information_version_pictures");

            migrationBuilder.DropTable(
                name: "teachers_information_versions");

            migrationBuilder.DropIndex(
                name: "IX_lesson_suggestions_rank_id_teachers_information_open",
                table: "lesson_suggestions");

            migrationBuilder.DropColumn(
                name: "teachers_notes",
                table: "lessons");

            migrationBuilder.DropColumn(
                name: "teachers_notes",
                table: "lesson_versions");

            migrationBuilder.DropColumn(
                name: "teachers_notes",
                table: "lesson_suggestions");

            migrationBuilder.DropColumn(
                name: "teachers_notes_only",
                table: "lesson_pictures");

            migrationBuilder.CreateIndex(
                name: "IX_lesson_suggestions_rank_id",
                table: "lesson_suggestions",
                column: "rank_id");
        }
    }
}
