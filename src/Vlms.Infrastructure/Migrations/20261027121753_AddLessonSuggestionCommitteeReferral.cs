using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vlms.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddLessonSuggestionCommitteeReferral : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "referral_reason",
                table: "lesson_suggestions",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "referred_at",
                table: "lesson_suggestions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "referred_by_user_id",
                table: "lesson_suggestions",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "lesson_suggestion_approvals",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false),
                    lesson_suggestion_id = table.Column<int>(type: "integer", nullable: false),
                    approver_user_id = table.Column<int>(type: "integer", nullable: false),
                    position = table.Column<int>(type: "integer", nullable: false),
                    approved_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_lesson_suggestion_approvals", x => x.id);
                    table.ForeignKey(
                        name: "FK_lesson_suggestion_approvals_app_users_approver_user_id",
                        column: x => x.approver_user_id,
                        principalTable: "app_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_lesson_suggestion_approvals_lesson_suggestions_lesson_sugge~",
                        column: x => x.lesson_suggestion_id,
                        principalTable: "lesson_suggestions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_lesson_suggestions_referred_by_user_id",
                table: "lesson_suggestions",
                column: "referred_by_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_lesson_suggestion_approvals_approver_user_id",
                table: "lesson_suggestion_approvals",
                column: "approver_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_lesson_suggestion_approvals_lesson_suggestion_id_approver_u~",
                table: "lesson_suggestion_approvals",
                columns: new[] { "lesson_suggestion_id", "approver_user_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_lesson_suggestion_approvals_lesson_suggestion_id_position",
                table: "lesson_suggestion_approvals",
                columns: new[] { "lesson_suggestion_id", "position" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_lesson_suggestions_app_users_referred_by_user_id",
                table: "lesson_suggestions",
                column: "referred_by_user_id",
                principalTable: "app_users",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_lesson_suggestions_app_users_referred_by_user_id",
                table: "lesson_suggestions");

            migrationBuilder.DropTable(
                name: "lesson_suggestion_approvals");

            migrationBuilder.DropIndex(
                name: "IX_lesson_suggestions_referred_by_user_id",
                table: "lesson_suggestions");

            migrationBuilder.DropColumn(
                name: "referral_reason",
                table: "lesson_suggestions");

            migrationBuilder.DropColumn(
                name: "referred_at",
                table: "lesson_suggestions");

            migrationBuilder.DropColumn(
                name: "referred_by_user_id",
                table: "lesson_suggestions");
        }
    }
}
