using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Vlms.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddChapterJoinRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "chapter_join_requests",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    user_id = table.Column<int>(type: "integer", nullable: false),
                    chapter_id = table.Column<int>(type: "integer", nullable: false),
                    joining_as = table.Column<int>(type: "integer", nullable: true),
                    requested_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    placed_by_user_id = table.Column<int>(type: "integer", nullable: true),
                    dismissed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    dismissed_by_user_id = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_chapter_join_requests", x => x.id);
                    table.ForeignKey(
                        name: "FK_chapter_join_requests_app_users_dismissed_by_user_id",
                        column: x => x.dismissed_by_user_id,
                        principalTable: "app_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_chapter_join_requests_app_users_placed_by_user_id",
                        column: x => x.placed_by_user_id,
                        principalTable: "app_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_chapter_join_requests_app_users_user_id",
                        column: x => x.user_id,
                        principalTable: "app_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_chapter_join_requests_chapters_chapter_id",
                        column: x => x.chapter_id,
                        principalTable: "chapters",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_chapter_join_requests_chapter_id_dismissed_at",
                table: "chapter_join_requests",
                columns: new[] { "chapter_id", "dismissed_at" });

            migrationBuilder.CreateIndex(
                name: "IX_chapter_join_requests_dismissed_by_user_id",
                table: "chapter_join_requests",
                column: "dismissed_by_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_chapter_join_requests_placed_by_user_id",
                table: "chapter_join_requests",
                column: "placed_by_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_chapter_join_requests_user_id",
                table: "chapter_join_requests",
                column: "user_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "chapter_join_requests");
        }
    }
}
