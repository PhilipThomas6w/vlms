using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vlms.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddLessonTeachersInformation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "teachers_information_pictures");

            migrationBuilder.DropColumn(
                name: "teachers_information",
                table: "ranks");

            migrationBuilder.AddColumn<string>(
                name: "teachers_information",
                table: "lessons",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "teachers_information",
                table: "lesson_versions",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "teachers_information",
                table: "lesson_suggestions",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "teachers_information_only",
                table: "lesson_pictures",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "teachers_information",
                table: "lessons");

            migrationBuilder.DropColumn(
                name: "teachers_information",
                table: "lesson_versions");

            migrationBuilder.DropColumn(
                name: "teachers_information",
                table: "lesson_suggestions");

            migrationBuilder.DropColumn(
                name: "teachers_information_only",
                table: "lesson_pictures");

            migrationBuilder.AddColumn<string>(
                name: "teachers_information",
                table: "ranks",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "teachers_information_pictures",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false),
                    rank_id = table.Column<int>(type: "integer", nullable: false),
                    blob_key = table.Column<string>(type: "text", nullable: false),
                    file_name = table.Column<string>(type: "text", nullable: false),
                    order = table.Column<int>(type: "integer", nullable: false)
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
                name: "IX_teachers_information_pictures_rank_id_file_name",
                table: "teachers_information_pictures",
                columns: new[] { "rank_id", "file_name" },
                unique: true);
        }
    }
}
