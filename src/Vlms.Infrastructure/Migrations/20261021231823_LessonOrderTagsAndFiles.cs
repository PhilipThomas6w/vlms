using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vlms.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class LessonOrderTagsAndFiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_lessons_rank_id",
                table: "lessons");

            migrationBuilder.AddColumn<int>(
                name: "order",
                table: "lessons",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            // Existing lessons get a delivery order by Code within their rank, and their single
            // content blob becomes their first file, before content_blob_key is dropped.
            migrationBuilder.Sql(
                """
                UPDATE lessons AS l SET "order" = o.rn
                FROM (SELECT id, ROW_NUMBER() OVER (PARTITION BY rank_id ORDER BY code, id) AS rn FROM lessons) AS o
                WHERE l.id = o.id;
                """);

            migrationBuilder.CreateTable(
                name: "lesson_files",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false),
                    lesson_id = table.Column<int>(type: "integer", nullable: false),
                    order = table.Column<int>(type: "integer", nullable: false),
                    file_name = table.Column<string>(type: "text", nullable: false),
                    blob_key = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_lesson_files", x => x.id);
                    table.ForeignKey(
                        name: "FK_lesson_files_lessons_lesson_id",
                        column: x => x.lesson_id,
                        principalTable: "lessons",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "lesson_tag_assignments",
                columns: table => new
                {
                    lesson_id = table.Column<int>(type: "integer", nullable: false),
                    tag = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_lesson_tag_assignments", x => new { x.lesson_id, x.tag });
                    table.ForeignKey(
                        name: "FK_lesson_tag_assignments_lessons_lesson_id",
                        column: x => x.lesson_id,
                        principalTable: "lessons",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.Sql(
                """
                INSERT INTO lesson_files (id, lesson_id, "order", file_name, blob_key)
                SELECT id, id, 1, content_blob_key, content_blob_key FROM lessons WHERE content_blob_key <> '';
                """);

            migrationBuilder.DropColumn(
                name: "content_blob_key",
                table: "lessons");

            migrationBuilder.CreateIndex(
                name: "IX_lessons_rank_id_order",
                table: "lessons",
                columns: new[] { "rank_id", "order" });

            migrationBuilder.CreateIndex(
                name: "IX_lesson_files_lesson_id",
                table: "lesson_files",
                column: "lesson_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "content_blob_key",
                table: "lessons",
                type: "text",
                nullable: false,
                defaultValue: "");

            // Lossy by nature: only each lesson's first file survives the downgrade.
            migrationBuilder.Sql(
                """
                UPDATE lessons AS l SET content_blob_key = f.blob_key
                FROM lesson_files AS f
                WHERE f.lesson_id = l.id AND f."order" = (SELECT MIN(f2."order") FROM lesson_files AS f2 WHERE f2.lesson_id = l.id);
                """);

            migrationBuilder.DropTable(
                name: "lesson_files");

            migrationBuilder.DropTable(
                name: "lesson_tag_assignments");

            migrationBuilder.DropIndex(
                name: "IX_lessons_rank_id_order",
                table: "lessons");

            migrationBuilder.DropColumn(
                name: "order",
                table: "lessons");

            migrationBuilder.CreateIndex(
                name: "IX_lessons_rank_id",
                table: "lessons",
                column: "rank_id");
        }
    }
}
