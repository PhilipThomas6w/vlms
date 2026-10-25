using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vlms.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAddedSaints : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "added_saints",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false),
                    month = table.Column<int>(type: "integer", nullable: false),
                    day = table.Column<int>(type: "integer", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    added_by_user_id = table.Column<int>(type: "integer", nullable: true),
                    added_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_added_saints", x => x.id);
                    table.ForeignKey(
                        name: "FK_added_saints_app_users_added_by_user_id",
                        column: x => x.added_by_user_id,
                        principalTable: "app_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_added_saints_added_by_user_id",
                table: "added_saints",
                column: "added_by_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_added_saints_month_day_name",
                table: "added_saints",
                columns: new[] { "month", "day", "name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "added_saints");
        }
    }
}
