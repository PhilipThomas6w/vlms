using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vlms.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAppUserLinkToStudentAndParentGuardian : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AppUserId",
                table: "Students",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "AppUserId",
                table: "ParentGuardians",
                type: "int",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Students_AppUserId",
                table: "Students",
                column: "AppUserId");

            migrationBuilder.CreateIndex(
                name: "IX_ParentGuardians_AppUserId",
                table: "ParentGuardians",
                column: "AppUserId");

            migrationBuilder.AddForeignKey(
                name: "FK_ParentGuardians_AppUsers_AppUserId",
                table: "ParentGuardians",
                column: "AppUserId",
                principalTable: "AppUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Students_AppUsers_AppUserId",
                table: "Students",
                column: "AppUserId",
                principalTable: "AppUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ParentGuardians_AppUsers_AppUserId",
                table: "ParentGuardians");

            migrationBuilder.DropForeignKey(
                name: "FK_Students_AppUsers_AppUserId",
                table: "Students");

            migrationBuilder.DropIndex(
                name: "IX_Students_AppUserId",
                table: "Students");

            migrationBuilder.DropIndex(
                name: "IX_ParentGuardians_AppUserId",
                table: "ParentGuardians");

            migrationBuilder.DropColumn(
                name: "AppUserId",
                table: "Students");

            migrationBuilder.DropColumn(
                name: "AppUserId",
                table: "ParentGuardians");
        }
    }
}
