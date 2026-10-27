using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Vlms.Infrastructure.Migrations
{
    /// <summary>
    /// functional.md FR-017 as amended 27/10/2026: the history of staff stepping down and being
    /// reinstated (<c>StaffStepDown</c>). Each account already stepped down gets its open period, dated
    /// from <c>left_at</c>, in the chapter holding its staff roles (Admin 0, Teacher 1, Safeguarding
    /// Officer 5); who recorded it is not known. PostgreSQL-only backfill, skipped on any other provider.
    /// </summary>
    public partial class AddStaffStepDowns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "staff_step_downs",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    user_id = table.Column<int>(type: "integer", nullable: false),
                    chapter_id = table.Column<int>(type: "integer", nullable: false),
                    stepped_down_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    stepped_down_by_user_id = table.Column<int>(type: "integer", nullable: true),
                    reinstated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    reinstated_by_user_id = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_staff_step_downs", x => x.id);
                    table.ForeignKey(
                        name: "FK_staff_step_downs_app_users_reinstated_by_user_id",
                        column: x => x.reinstated_by_user_id,
                        principalTable: "app_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_staff_step_downs_app_users_stepped_down_by_user_id",
                        column: x => x.stepped_down_by_user_id,
                        principalTable: "app_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_staff_step_downs_app_users_user_id",
                        column: x => x.user_id,
                        principalTable: "app_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_staff_step_downs_chapters_chapter_id",
                        column: x => x.chapter_id,
                        principalTable: "chapters",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_staff_step_downs_chapter_id",
                table: "staff_step_downs",
                column: "chapter_id");

            migrationBuilder.CreateIndex(
                name: "IX_staff_step_downs_reinstated_by_user_id",
                table: "staff_step_downs",
                column: "reinstated_by_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_staff_step_downs_stepped_down_by_user_id",
                table: "staff_step_downs",
                column: "stepped_down_by_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_staff_step_downs_user_id_stepped_down_at",
                table: "staff_step_downs",
                columns: new[] { "user_id", "stepped_down_at" });

            if (migrationBuilder.ActiveProvider != "Npgsql.EntityFrameworkCore.PostgreSQL")
            {
                return;
            }

            migrationBuilder.Sql(
                "INSERT INTO staff_step_downs (user_id, chapter_id, stepped_down_at) " +
                "SELECT u.id, MIN(r.chapter_id), u.left_at FROM app_users u " +
                "JOIN user_roles r ON r.user_id = u.id AND r.role IN (0, 1, 5) AND r.chapter_id IS NOT NULL " +
                "WHERE u.has_left AND u.left_at IS NOT NULL GROUP BY u.id, u.left_at;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "staff_step_downs");
        }
    }
}
