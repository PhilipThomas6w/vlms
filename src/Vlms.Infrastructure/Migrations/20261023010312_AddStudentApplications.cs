using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vlms.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddStudentApplications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "relationship",
                table: "student_guardian_links",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "phone",
                table: "parent_guardians",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "postal_address",
                table: "parent_guardians",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "student_applications",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false),
                    applicant_user_id = table.Column<int>(type: "integer", nullable: false),
                    parent_phone = table.Column<string>(type: "text", nullable: false),
                    parent_postal_address = table.Column<string>(type: "text", nullable: false),
                    relationship = table.Column<string>(type: "text", nullable: false),
                    child_name = table.Column<string>(type: "text", nullable: false),
                    child_date_of_birth = table.Column<DateOnly>(type: "date", nullable: false),
                    photo_media_consent = table.Column<bool>(type: "boolean", nullable: false),
                    transport_consent = table.Column<bool>(type: "boolean", nullable: false),
                    offsite_trip_consent = table.Column<bool>(type: "boolean", nullable: false),
                    data_sharing_consent = table.Column<bool>(type: "boolean", nullable: false),
                    status = table.Column<int>(type: "integer", nullable: false),
                    submitted_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    last_submitted_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    safeguarding_reviewed_by_user_id = table.Column<int>(type: "integer", nullable: true),
                    safeguarding_reviewed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    admin_decided_by_user_id = table.Column<int>(type: "integer", nullable: true),
                    admin_decided_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    student_id = table.Column<int>(type: "integer", nullable: true),
                    linked_to_existing_student = table.Column<bool>(type: "boolean", nullable: false),
                    consent_record_id = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_student_applications", x => x.id);
                    table.ForeignKey(
                        name: "FK_student_applications_app_users_admin_decided_by_user_id",
                        column: x => x.admin_decided_by_user_id,
                        principalTable: "app_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_student_applications_app_users_applicant_user_id",
                        column: x => x.applicant_user_id,
                        principalTable: "app_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_student_applications_app_users_safeguarding_reviewed_by_use~",
                        column: x => x.safeguarding_reviewed_by_user_id,
                        principalTable: "app_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_student_applications_consent_records_consent_record_id",
                        column: x => x.consent_record_id,
                        principalTable: "consent_records",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_student_applications_students_student_id",
                        column: x => x.student_id,
                        principalTable: "students",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "student_application_sensitive_details",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false),
                    student_application_id = table.Column<int>(type: "integer", nullable: false),
                    emergency_contact = table.Column<string>(type: "text", nullable: false),
                    emergency_medical_info = table.Column<string>(type: "text", nullable: true),
                    dietary_requirements = table.Column<string>(type: "text", nullable: true),
                    special_educational_needs = table.Column<string>(type: "text", nullable: true),
                    information_request = table.Column<string>(type: "text", nullable: true),
                    parent_response = table.Column<string>(type: "text", nullable: true),
                    decision_comments = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_student_application_sensitive_details", x => x.id);
                    table.ForeignKey(
                        name: "FK_student_application_sensitive_details_student_applications_~",
                        column: x => x.student_application_id,
                        principalTable: "student_applications",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_student_application_sensitive_details_student_application_id",
                table: "student_application_sensitive_details",
                column: "student_application_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_student_applications_admin_decided_by_user_id",
                table: "student_applications",
                column: "admin_decided_by_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_student_applications_applicant_user_id_status",
                table: "student_applications",
                columns: new[] { "applicant_user_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_student_applications_consent_record_id",
                table: "student_applications",
                column: "consent_record_id");

            migrationBuilder.CreateIndex(
                name: "IX_student_applications_safeguarding_reviewed_by_user_id",
                table: "student_applications",
                column: "safeguarding_reviewed_by_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_student_applications_status",
                table: "student_applications",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "IX_student_applications_student_id",
                table: "student_applications",
                column: "student_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "student_application_sensitive_details");

            migrationBuilder.DropTable(
                name: "student_applications");

            migrationBuilder.DropColumn(
                name: "relationship",
                table: "student_guardian_links");

            migrationBuilder.DropColumn(
                name: "phone",
                table: "parent_guardians");

            migrationBuilder.DropColumn(
                name: "postal_address",
                table: "parent_guardians");
        }
    }
}
