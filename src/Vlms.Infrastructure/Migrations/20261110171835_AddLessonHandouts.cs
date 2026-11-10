using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vlms.Infrastructure.Migrations
{
    /// <summary>
    /// Lesson handouts (docs/adr/0016): a lesson's files become its handouts, with a display name of their own, kept per
    /// lesson version and carried by lesson suggestions. Existing file rows keep their ids (saved copies on devices refer to
    /// them); their display name is the stored name without its extension, as the library showed it before. Earlier versions
    /// are not given handouts: the existing files were never part of a reviewed version, and once the audit is confirmed
    /// the curriculum seeder replaces them with the kept handouts (adr/0016 decision 4).
    /// </summary>
    public partial class AddLessonHandouts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "display_name",
                table: "lesson_files",
                type: "text",
                nullable: false,
                defaultValue: "");

            // The name the library showed: the stored name without its last extension (or the whole name, if that is all).
            migrationBuilder.Sql(
                "UPDATE lesson_files SET display_name = CASE WHEN regexp_replace(file_name, '\\.[^.]*$', '') = '' " +
                "THEN file_name ELSE regexp_replace(file_name, '\\.[^.]*$', '') END;");

            migrationBuilder.CreateTable(
                name: "lesson_suggestion_handouts",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false),
                    lesson_suggestion_id = table.Column<int>(type: "integer", nullable: false),
                    order = table.Column<int>(type: "integer", nullable: false),
                    file_name = table.Column<string>(type: "text", nullable: false),
                    display_name = table.Column<string>(type: "text", nullable: false),
                    blob_key = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_lesson_suggestion_handouts", x => x.id);
                    table.ForeignKey(
                        name: "FK_lesson_suggestion_handouts_lesson_suggestions_lesson_sugges~",
                        column: x => x.lesson_suggestion_id,
                        principalTable: "lesson_suggestions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "lesson_version_handouts",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false),
                    lesson_version_id = table.Column<int>(type: "integer", nullable: false),
                    order = table.Column<int>(type: "integer", nullable: false),
                    file_name = table.Column<string>(type: "text", nullable: false),
                    display_name = table.Column<string>(type: "text", nullable: false),
                    blob_key = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_lesson_version_handouts", x => x.id);
                    table.ForeignKey(
                        name: "FK_lesson_version_handouts_lesson_versions_lesson_version_id",
                        column: x => x.lesson_version_id,
                        principalTable: "lesson_versions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_lesson_suggestion_handouts_lesson_suggestion_id_blob_key",
                table: "lesson_suggestion_handouts",
                columns: new[] { "lesson_suggestion_id", "blob_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_lesson_version_handouts_lesson_version_id_blob_key",
                table: "lesson_version_handouts",
                columns: new[] { "lesson_version_id", "blob_key" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "lesson_suggestion_handouts");

            migrationBuilder.DropTable(
                name: "lesson_version_handouts");

            migrationBuilder.DropColumn(
                name: "display_name",
                table: "lesson_files");
        }
    }
}
