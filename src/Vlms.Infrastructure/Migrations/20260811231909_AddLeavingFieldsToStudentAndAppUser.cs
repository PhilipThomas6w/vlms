using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vlms.Infrastructure.Migrations
{
    /// <summary>
    /// STATE.md "Manual 'leaving' flag for students and teachers, feeding the retention report" —
    /// adds <c>Student.LeftAt</c> (nullable, set alongside <c>Status = Inactive</c> by
    /// <c>Retention.LeavingService.MarkStudentInactiveAsync</c>) and <c>AppUser.HasLeft</c>/
    /// <c>AppUser.LeftAt</c> (set by <c>LeavingService.MarkTeacherLeftAsync</c>, generic to any
    /// <c>AppUser</c> row — see <c>AppUser.HasLeft</c>'s own doc comment for the schema-placement
    /// judgement call). Pure additive column changes — no existing column/table touched, no data
    /// migration needed (every existing row defaults to "not left").
    /// </summary>
    public partial class AddLeavingFieldsToStudentAndAppUser : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "left_at",
                table: "students",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "has_left",
                table: "app_users",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "left_at",
                table: "app_users",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "left_at",
                table: "students");

            migrationBuilder.DropColumn(
                name: "has_left",
                table: "app_users");

            migrationBuilder.DropColumn(
                name: "left_at",
                table: "app_users");
        }
    }
}
