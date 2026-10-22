using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vlms.Infrastructure.Migrations
{
    /// <summary>
    /// adr/0008 decision 5. Existing rows: both new consent flags take the old combined flag's value;
    /// the old combined dietary/SEN text becomes dietary requirements and special educational needs
    /// starts empty. Columns are renamed in place (not dropped and re-added) so no data is lost.
    /// </summary>
    public partial class SplitConsentTransportAndDietaryFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "transport_offsite_consent",
                table: "consent_records",
                newName: "transport_consent");

            migrationBuilder.AddColumn<bool>(
                name: "offsite_trip_consent",
                table: "consent_records",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // The old flag covered both, so the off-site flag starts with the same value.
            migrationBuilder.Sql("UPDATE consent_records SET offsite_trip_consent = transport_consent;");

            migrationBuilder.RenameColumn(
                name: "dietary_sen",
                table: "consent_sensitive_details",
                newName: "dietary_requirements");

            migrationBuilder.AddColumn<string>(
                name: "special_educational_needs",
                table: "consent_sensitive_details",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Lossy by nature: the old combined flag is true only when both were consented to, and
            // the old combined text joins the two fields.
            migrationBuilder.Sql(
                "UPDATE consent_records SET transport_consent = (transport_consent AND offsite_trip_consent);");

            migrationBuilder.Sql(
                "UPDATE consent_sensitive_details SET dietary_requirements = " +
                "NULLIF(CONCAT_WS(E'\\n', dietary_requirements, special_educational_needs), '');");

            migrationBuilder.DropColumn(
                name: "offsite_trip_consent",
                table: "consent_records");

            migrationBuilder.DropColumn(
                name: "special_educational_needs",
                table: "consent_sensitive_details");

            migrationBuilder.RenameColumn(
                name: "transport_consent",
                table: "consent_records",
                newName: "transport_offsite_consent");

            migrationBuilder.RenameColumn(
                name: "dietary_requirements",
                table: "consent_sensitive_details",
                newName: "dietary_sen");
        }
    }
}
