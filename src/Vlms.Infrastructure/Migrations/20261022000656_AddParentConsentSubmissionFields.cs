using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vlms.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddParentConsentSubmissionFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "decided_at",
                table: "consent_records",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "decision_comments",
                table: "consent_records",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "student_date_of_birth",
                table: "consent_records",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "student_name",
                table: "consent_records",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "submitted_at",
                table: "consent_records",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "submitted_by_user_id",
                table: "consent_records",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_consent_records_submitted_by_user_id",
                table: "consent_records",
                column: "submitted_by_user_id");

            migrationBuilder.AddForeignKey(
                name: "FK_consent_records_app_users_submitted_by_user_id",
                table: "consent_records",
                column: "submitted_by_user_id",
                principalTable: "app_users",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_consent_records_app_users_submitted_by_user_id",
                table: "consent_records");

            migrationBuilder.DropIndex(
                name: "IX_consent_records_submitted_by_user_id",
                table: "consent_records");

            migrationBuilder.DropColumn(
                name: "decided_at",
                table: "consent_records");

            migrationBuilder.DropColumn(
                name: "decision_comments",
                table: "consent_records");

            migrationBuilder.DropColumn(
                name: "student_date_of_birth",
                table: "consent_records");

            migrationBuilder.DropColumn(
                name: "student_name",
                table: "consent_records");

            migrationBuilder.DropColumn(
                name: "submitted_at",
                table: "consent_records");

            migrationBuilder.DropColumn(
                name: "submitted_by_user_id",
                table: "consent_records");
        }
    }
}
