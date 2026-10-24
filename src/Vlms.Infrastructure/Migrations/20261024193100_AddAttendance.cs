using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vlms.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAttendance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "attendance_registers",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false),
                    chapter_id = table.Column<int>(type: "integer", nullable: false),
                    meeting_date = table.Column<DateOnly>(type: "date", nullable: false),
                    taken_by_user_id = table.Column<int>(type: "integer", nullable: false),
                    taken_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_attendance_registers", x => x.id);
                    table.ForeignKey(
                        name: "FK_attendance_registers_app_users_taken_by_user_id",
                        column: x => x.taken_by_user_id,
                        principalTable: "app_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_attendance_registers_chapters_chapter_id",
                        column: x => x.chapter_id,
                        principalTable: "chapters",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "attendance_marks",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false),
                    chapter_id = table.Column<int>(type: "integer", nullable: false),
                    attendance_register_id = table.Column<int>(type: "integer", nullable: false),
                    student_id = table.Column<int>(type: "integer", nullable: false),
                    meeting_date = table.Column<DateOnly>(type: "date", nullable: false),
                    present = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_attendance_marks", x => x.id);
                    table.ForeignKey(
                        name: "FK_attendance_marks_attendance_registers_attendance_register_id",
                        column: x => x.attendance_register_id,
                        principalTable: "attendance_registers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_attendance_marks_chapters_chapter_id",
                        column: x => x.chapter_id,
                        principalTable: "chapters",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_attendance_marks_students_student_id",
                        column: x => x.student_id,
                        principalTable: "students",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_attendance_marks_attendance_register_id_student_id",
                table: "attendance_marks",
                columns: new[] { "attendance_register_id", "student_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_attendance_marks_chapter_id",
                table: "attendance_marks",
                column: "chapter_id");

            migrationBuilder.CreateIndex(
                name: "IX_attendance_marks_student_id_meeting_date",
                table: "attendance_marks",
                columns: new[] { "student_id", "meeting_date" });

            migrationBuilder.CreateIndex(
                name: "IX_attendance_registers_chapter_id",
                table: "attendance_registers",
                column: "chapter_id");

            migrationBuilder.CreateIndex(
                name: "IX_attendance_registers_chapter_id_meeting_date",
                table: "attendance_registers",
                columns: new[] { "chapter_id", "meeting_date" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_attendance_registers_taken_by_user_id",
                table: "attendance_registers",
                column: "taken_by_user_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "attendance_marks");

            migrationBuilder.DropTable(
                name: "attendance_registers");
        }
    }
}
