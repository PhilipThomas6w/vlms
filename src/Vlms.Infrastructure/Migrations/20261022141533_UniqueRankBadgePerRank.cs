using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vlms.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class UniqueRankBadgePerRank : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_rank_badges_rank_id",
                table: "rank_badges");

            migrationBuilder.CreateIndex(
                name: "IX_rank_badges_rank_id",
                table: "rank_badges",
                column: "rank_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_rank_badges_rank_id",
                table: "rank_badges");

            migrationBuilder.CreateIndex(
                name: "IX_rank_badges_rank_id",
                table: "rank_badges",
                column: "rank_id");
        }
    }
}
