using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vlms.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAppHostedLessons : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "teachers_information",
                table: "ranks",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "body",
                table: "lessons",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "content_version",
                table: "lessons",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "lesson_pictures",
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
                    table.PrimaryKey("PK_lesson_pictures", x => x.id);
                    table.ForeignKey(
                        name: "FK_lesson_pictures_lessons_lesson_id",
                        column: x => x.lesson_id,
                        principalTable: "lessons",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "teachers_information_pictures",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false),
                    rank_id = table.Column<int>(type: "integer", nullable: false),
                    order = table.Column<int>(type: "integer", nullable: false),
                    file_name = table.Column<string>(type: "text", nullable: false),
                    blob_key = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_teachers_information_pictures", x => x.id);
                    table.ForeignKey(
                        name: "FK_teachers_information_pictures_ranks_rank_id",
                        column: x => x.rank_id,
                        principalTable: "ranks",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_lesson_pictures_lesson_id_file_name",
                table: "lesson_pictures",
                columns: new[] { "lesson_id", "file_name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_teachers_information_pictures_rank_id_file_name",
                table: "teachers_information_pictures",
                columns: new[] { "rank_id", "file_name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "lesson_pictures");

            migrationBuilder.DropTable(
                name: "teachers_information_pictures");

            migrationBuilder.DropColumn(
                name: "teachers_information",
                table: "ranks");

            migrationBuilder.DropColumn(
                name: "body",
                table: "lessons");

            migrationBuilder.DropColumn(
                name: "content_version",
                table: "lessons");
        }
    }
}
