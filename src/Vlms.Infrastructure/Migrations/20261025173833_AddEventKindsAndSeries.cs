using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vlms.Infrastructure.Migrations
{
    /// <summary>
    /// Event kinds and repeating series (functional.md FR-022, Philip 25/10/2026). Every existing event
    /// becomes a class (kind 1) and every existing mark a class session, which is what they were; an
    /// excursion's register has no rank; one class or excursion a day per chapter, parish events unlimited.
    /// </summary>
    public partial class AddEventKindsAndSeries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_chapter_events_chapter_id_date",
                table: "chapter_events");

            migrationBuilder.AddColumn<int>(
                name: "kind",
                table: "chapter_events",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "series_id",
                table: "chapter_events",
                type: "integer",
                nullable: true);

            migrationBuilder.AlterColumn<int>(
                name: "rank_id",
                table: "attendance_registers",
                type: "integer",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.AddColumn<bool>(
                name: "class_session",
                table: "attendance_marks",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.CreateIndex(
                name: "IX_chapter_events_chapter_id_date",
                table: "chapter_events",
                columns: new[] { "chapter_id", "date" },
                unique: true,
                filter: "kind IN (1, 2)");

            migrationBuilder.CreateIndex(
                name: "IX_chapter_events_series_id",
                table: "chapter_events",
                column: "series_id");

            migrationBuilder.CreateIndex(
                name: "IX_attendance_registers_chapter_event_id_everyone",
                table: "attendance_registers",
                column: "chapter_event_id",
                unique: true,
                filter: "rank_id IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_chapter_events_chapter_id_date",
                table: "chapter_events");

            migrationBuilder.DropIndex(
                name: "IX_chapter_events_series_id",
                table: "chapter_events");

            migrationBuilder.DropIndex(
                name: "IX_attendance_registers_chapter_event_id_everyone",
                table: "attendance_registers");

            migrationBuilder.DropColumn(
                name: "kind",
                table: "chapter_events");

            migrationBuilder.DropColumn(
                name: "series_id",
                table: "chapter_events");

            migrationBuilder.DropColumn(
                name: "class_session",
                table: "attendance_marks");

            migrationBuilder.AlterColumn<int>(
                name: "rank_id",
                table: "attendance_registers",
                type: "integer",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_chapter_events_chapter_id_date",
                table: "chapter_events",
                columns: new[] { "chapter_id", "date" },
                unique: true);
        }
    }
}
