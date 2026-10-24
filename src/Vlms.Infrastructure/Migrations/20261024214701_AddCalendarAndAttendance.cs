using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vlms.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCalendarAndAttendance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "calendar_feed_tokens",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false),
                    user_id = table.Column<int>(type: "integer", nullable: false),
                    chapter_id = table.Column<int>(type: "integer", nullable: false),
                    token_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_calendar_feed_tokens", x => x.id);
                    table.ForeignKey(
                        name: "FK_calendar_feed_tokens_app_users_user_id",
                        column: x => x.user_id,
                        principalTable: "app_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_calendar_feed_tokens_chapters_chapter_id",
                        column: x => x.chapter_id,
                        principalTable: "chapters",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "chapter_events",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false),
                    chapter_id = table.Column<int>(type: "integer", nullable: false),
                    date = table.Column<DateOnly>(type: "date", nullable: false),
                    title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    start_time = table.Column<TimeOnly>(type: "time without time zone", nullable: true),
                    end_time = table.Column<TimeOnly>(type: "time without time zone", nullable: true),
                    location = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    created_by_user_id = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_chapter_events", x => x.id);
                    table.ForeignKey(
                        name: "FK_chapter_events_app_users_created_by_user_id",
                        column: x => x.created_by_user_id,
                        principalTable: "app_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_chapter_events_chapters_chapter_id",
                        column: x => x.chapter_id,
                        principalTable: "chapters",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "class_teachers",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false),
                    chapter_id = table.Column<int>(type: "integer", nullable: false),
                    rank_id = table.Column<int>(type: "integer", nullable: false),
                    teacher_user_id = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_class_teachers", x => x.id);
                    table.ForeignKey(
                        name: "FK_class_teachers_app_users_teacher_user_id",
                        column: x => x.teacher_user_id,
                        principalTable: "app_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_class_teachers_chapters_chapter_id",
                        column: x => x.chapter_id,
                        principalTable: "chapters",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_class_teachers_ranks_rank_id",
                        column: x => x.rank_id,
                        principalTable: "ranks",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "liturgical_days",
                columns: table => new
                {
                    date = table.Column<DateOnly>(type: "date", nullable: false),
                    title = table.Column<string>(type: "text", nullable: false),
                    fast = table.Column<int>(type: "integer", nullable: false),
                    saints = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_liturgical_days", x => x.date);
                });

            migrationBuilder.CreateTable(
                name: "attendance_registers",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false),
                    chapter_id = table.Column<int>(type: "integer", nullable: false),
                    chapter_event_id = table.Column<int>(type: "integer", nullable: false),
                    rank_id = table.Column<int>(type: "integer", nullable: false),
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
                        name: "FK_attendance_registers_chapter_events_chapter_event_id",
                        column: x => x.chapter_event_id,
                        principalTable: "chapter_events",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_attendance_registers_chapters_chapter_id",
                        column: x => x.chapter_id,
                        principalTable: "chapters",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_attendance_registers_ranks_rank_id",
                        column: x => x.rank_id,
                        principalTable: "ranks",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "liturgical_readings",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false),
                    date = table.Column<DateOnly>(type: "date", nullable: false),
                    order = table.Column<int>(type: "integer", nullable: false),
                    heading = table.Column<string>(type: "text", nullable: false),
                    reference = table.Column<string>(type: "text", nullable: false),
                    text = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_liturgical_readings", x => x.id);
                    table.ForeignKey(
                        name: "FK_liturgical_readings_liturgical_days_date",
                        column: x => x.date,
                        principalTable: "liturgical_days",
                        principalColumn: "date",
                        onDelete: ReferentialAction.Cascade);
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
                columns: new[] { "student_id", "meeting_date" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_attendance_registers_chapter_event_id_rank_id",
                table: "attendance_registers",
                columns: new[] { "chapter_event_id", "rank_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_attendance_registers_chapter_id",
                table: "attendance_registers",
                column: "chapter_id");

            migrationBuilder.CreateIndex(
                name: "IX_attendance_registers_rank_id",
                table: "attendance_registers",
                column: "rank_id");

            migrationBuilder.CreateIndex(
                name: "IX_attendance_registers_taken_by_user_id",
                table: "attendance_registers",
                column: "taken_by_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_calendar_feed_tokens_chapter_id",
                table: "calendar_feed_tokens",
                column: "chapter_id");

            migrationBuilder.CreateIndex(
                name: "IX_calendar_feed_tokens_token_hash",
                table: "calendar_feed_tokens",
                column: "token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_calendar_feed_tokens_user_id_chapter_id",
                table: "calendar_feed_tokens",
                columns: new[] { "user_id", "chapter_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_chapter_events_chapter_id",
                table: "chapter_events",
                column: "chapter_id");

            migrationBuilder.CreateIndex(
                name: "IX_chapter_events_chapter_id_date",
                table: "chapter_events",
                columns: new[] { "chapter_id", "date" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_chapter_events_created_by_user_id",
                table: "chapter_events",
                column: "created_by_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_class_teachers_chapter_id",
                table: "class_teachers",
                column: "chapter_id");

            migrationBuilder.CreateIndex(
                name: "IX_class_teachers_chapter_id_rank_id_teacher_user_id",
                table: "class_teachers",
                columns: new[] { "chapter_id", "rank_id", "teacher_user_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_class_teachers_rank_id",
                table: "class_teachers",
                column: "rank_id");

            migrationBuilder.CreateIndex(
                name: "IX_class_teachers_teacher_user_id",
                table: "class_teachers",
                column: "teacher_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_liturgical_readings_date_order",
                table: "liturgical_readings",
                columns: new[] { "date", "order" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "attendance_marks");

            migrationBuilder.DropTable(
                name: "calendar_feed_tokens");

            migrationBuilder.DropTable(
                name: "class_teachers");

            migrationBuilder.DropTable(
                name: "liturgical_readings");

            migrationBuilder.DropTable(
                name: "attendance_registers");

            migrationBuilder.DropTable(
                name: "liturgical_days");

            migrationBuilder.DropTable(
                name: "chapter_events");
        }
    }
}
