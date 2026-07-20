using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Vlms.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "app_users",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false),
                    entra_object_id = table.Column<string>(type: "text", nullable: false),
                    display_name = table.Column<string>(type: "text", nullable: false),
                    email = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_app_users", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "ranks",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false),
                    order = table.Column<int>(type: "integer", nullable: false),
                    code = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ranks", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "sensitive_data_access_logs",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    user_id = table.Column<int>(type: "integer", nullable: true),
                    entity = table.Column<string>(type: "text", nullable: false),
                    entity_id = table.Column<int>(type: "integer", nullable: false),
                    accessed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    access_type = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sensitive_data_access_logs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "dbs_checks",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false),
                    teacher_user_id = table.Column<int>(type: "integer", nullable: false),
                    check_date = table.Column<DateOnly>(type: "date", nullable: false),
                    expiry_date = table.Column<DateOnly>(type: "date", nullable: false),
                    certificate_number = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_dbs_checks", x => x.id);
                    table.ForeignKey(
                        name: "FK_dbs_checks_app_users_teacher_user_id",
                        column: x => x.teacher_user_id,
                        principalTable: "app_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "parent_guardians",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    contact_info = table.Column<string>(type: "text", nullable: false),
                    is_primary = table.Column<bool>(type: "boolean", nullable: false),
                    app_user_id = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_parent_guardians", x => x.id);
                    table.ForeignKey(
                        name: "FK_parent_guardians_app_users_app_user_id",
                        column: x => x.app_user_id,
                        principalTable: "app_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "user_roles",
                columns: table => new
                {
                    user_id = table.Column<int>(type: "integer", nullable: false),
                    role = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_roles", x => new { x.user_id, x.role });
                    table.ForeignKey(
                        name: "FK_user_roles_app_users_user_id",
                        column: x => x.user_id,
                        principalTable: "app_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "lessons",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false),
                    rank_id = table.Column<int>(type: "integer", nullable: false),
                    code = table.Column<string>(type: "text", nullable: false),
                    title = table.Column<string>(type: "text", nullable: false),
                    content_blob_key = table.Column<string>(type: "text", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_lessons", x => x.id);
                    table.ForeignKey(
                        name: "FK_lessons_ranks_rank_id",
                        column: x => x.rank_id,
                        principalTable: "ranks",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "rank_badges",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false),
                    rank_id = table.Column<int>(type: "integer", nullable: false),
                    image_blob_key = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_rank_badges", x => x.id);
                    table.ForeignKey(
                        name: "FK_rank_badges_ranks_rank_id",
                        column: x => x.rank_id,
                        principalTable: "ranks",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "students",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    date_of_birth = table.Column<DateOnly>(type: "date", nullable: false),
                    current_rank_id = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<int>(type: "integer", nullable: false),
                    enrolment_date = table.Column<DateOnly>(type: "date", nullable: false),
                    assigned_teacher_user_id = table.Column<int>(type: "integer", nullable: true),
                    app_user_id = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_students", x => x.id);
                    table.ForeignKey(
                        name: "FK_students_app_users_app_user_id",
                        column: x => x.app_user_id,
                        principalTable: "app_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_students_app_users_assigned_teacher_user_id",
                        column: x => x.assigned_teacher_user_id,
                        principalTable: "app_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_students_ranks_current_rank_id",
                        column: x => x.current_rank_id,
                        principalTable: "ranks",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "lesson_change_proposals",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false),
                    lesson_id = table.Column<int>(type: "integer", nullable: true),
                    proposed_by_user_id = table.Column<int>(type: "integer", nullable: false),
                    change_type = table.Column<int>(type: "integer", nullable: false),
                    proposed_content = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<int>(type: "integer", nullable: false),
                    approver_user_id = table.Column<int>(type: "integer", nullable: true),
                    approval_comments = table.Column<string>(type: "text", nullable: true),
                    submitted_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    decided_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    resubmission_of_proposal_id = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_lesson_change_proposals", x => x.id);
                    table.ForeignKey(
                        name: "FK_lesson_change_proposals_app_users_approver_user_id",
                        column: x => x.approver_user_id,
                        principalTable: "app_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_lesson_change_proposals_app_users_proposed_by_user_id",
                        column: x => x.proposed_by_user_id,
                        principalTable: "app_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_lesson_change_proposals_lesson_change_proposals_resubmissio~",
                        column: x => x.resubmission_of_proposal_id,
                        principalTable: "lesson_change_proposals",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_lesson_change_proposals_lessons_lesson_id",
                        column: x => x.lesson_id,
                        principalTable: "lessons",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "consent_records",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false),
                    student_id = table.Column<int>(type: "integer", nullable: false),
                    period_start = table.Column<DateOnly>(type: "date", nullable: false),
                    period_end = table.Column<DateOnly>(type: "date", nullable: false),
                    photo_media_consent = table.Column<bool>(type: "boolean", nullable: false),
                    transport_offsite_consent = table.Column<bool>(type: "boolean", nullable: false),
                    data_sharing_consent = table.Column<bool>(type: "boolean", nullable: false),
                    status = table.Column<int>(type: "integer", nullable: false),
                    submitted_by_parent_id = table.Column<int>(type: "integer", nullable: false),
                    approved_by_user_id = table.Column<int>(type: "integer", nullable: true),
                    expiry_date = table.Column<DateOnly>(type: "date", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_consent_records", x => x.id);
                    table.ForeignKey(
                        name: "FK_consent_records_app_users_approved_by_user_id",
                        column: x => x.approved_by_user_id,
                        principalTable: "app_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_consent_records_parent_guardians_submitted_by_parent_id",
                        column: x => x.submitted_by_parent_id,
                        principalTable: "parent_guardians",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_consent_records_students_student_id",
                        column: x => x.student_id,
                        principalTable: "students",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "student_badges",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false),
                    student_id = table.Column<int>(type: "integer", nullable: false),
                    rank_badge_id = table.Column<int>(type: "integer", nullable: false),
                    awarded_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_student_badges", x => x.id);
                    table.ForeignKey(
                        name: "FK_student_badges_rank_badges_rank_badge_id",
                        column: x => x.rank_badge_id,
                        principalTable: "rank_badges",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_student_badges_students_student_id",
                        column: x => x.student_id,
                        principalTable: "students",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "student_guardian_links",
                columns: table => new
                {
                    student_id = table.Column<int>(type: "integer", nullable: false),
                    parent_guardian_id = table.Column<int>(type: "integer", nullable: false),
                    created_by_user_id = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_student_guardian_links", x => new { x.student_id, x.parent_guardian_id });
                    table.ForeignKey(
                        name: "FK_student_guardian_links_app_users_created_by_user_id",
                        column: x => x.created_by_user_id,
                        principalTable: "app_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_student_guardian_links_parent_guardians_parent_guardian_id",
                        column: x => x.parent_guardian_id,
                        principalTable: "parent_guardians",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_student_guardian_links_students_student_id",
                        column: x => x.student_id,
                        principalTable: "students",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "student_lesson_completions",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false),
                    student_id = table.Column<int>(type: "integer", nullable: false),
                    lesson_id = table.Column<int>(type: "integer", nullable: false),
                    completed_by_user_id = table.Column<int>(type: "integer", nullable: false),
                    completed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    note = table.Column<string>(type: "text", nullable: true),
                    is_reversed = table.Column<bool>(type: "boolean", nullable: false),
                    reversed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_student_lesson_completions", x => x.id);
                    table.ForeignKey(
                        name: "FK_student_lesson_completions_app_users_completed_by_user_id",
                        column: x => x.completed_by_user_id,
                        principalTable: "app_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_student_lesson_completions_lessons_lesson_id",
                        column: x => x.lesson_id,
                        principalTable: "lessons",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_student_lesson_completions_students_student_id",
                        column: x => x.student_id,
                        principalTable: "students",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "student_rank_progresses",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false),
                    student_id = table.Column<int>(type: "integer", nullable: false),
                    rank_id = table.Column<int>(type: "integer", nullable: false),
                    started_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_student_rank_progresses", x => x.id);
                    table.ForeignKey(
                        name: "FK_student_rank_progresses_ranks_rank_id",
                        column: x => x.rank_id,
                        principalTable: "ranks",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_student_rank_progresses_students_student_id",
                        column: x => x.student_id,
                        principalTable: "students",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "consent_sensitive_details",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false),
                    consent_record_id = table.Column<int>(type: "integer", nullable: false),
                    emergency_medical_info = table.Column<string>(type: "text", nullable: true),
                    dietary_sen = table.Column<string>(type: "text", nullable: true),
                    emergency_contact = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_consent_sensitive_details", x => x.id);
                    table.ForeignKey(
                        name: "FK_consent_sensitive_details_consent_records_consent_record_id",
                        column: x => x.consent_record_id,
                        principalTable: "consent_records",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "certificates",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false),
                    student_lesson_completion_id = table.Column<int>(type: "integer", nullable: false),
                    generated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    blob_key = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_certificates", x => x.id);
                    table.ForeignKey(
                        name: "FK_certificates_student_lesson_completions_student_lesson_comp~",
                        column: x => x.student_lesson_completion_id,
                        principalTable: "student_lesson_completions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_certificates_student_lesson_completion_id",
                table: "certificates",
                column: "student_lesson_completion_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_consent_records_approved_by_user_id",
                table: "consent_records",
                column: "approved_by_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_consent_records_student_id",
                table: "consent_records",
                column: "student_id");

            migrationBuilder.CreateIndex(
                name: "IX_consent_records_submitted_by_parent_id",
                table: "consent_records",
                column: "submitted_by_parent_id");

            migrationBuilder.CreateIndex(
                name: "IX_consent_sensitive_details_consent_record_id",
                table: "consent_sensitive_details",
                column: "consent_record_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_dbs_checks_teacher_user_id",
                table: "dbs_checks",
                column: "teacher_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_lesson_change_proposals_approver_user_id",
                table: "lesson_change_proposals",
                column: "approver_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_lesson_change_proposals_lesson_id",
                table: "lesson_change_proposals",
                column: "lesson_id");

            migrationBuilder.CreateIndex(
                name: "IX_lesson_change_proposals_proposed_by_user_id",
                table: "lesson_change_proposals",
                column: "proposed_by_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_lesson_change_proposals_resubmission_of_proposal_id",
                table: "lesson_change_proposals",
                column: "resubmission_of_proposal_id");

            migrationBuilder.CreateIndex(
                name: "IX_lessons_rank_id",
                table: "lessons",
                column: "rank_id");

            migrationBuilder.CreateIndex(
                name: "IX_parent_guardians_app_user_id",
                table: "parent_guardians",
                column: "app_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_rank_badges_rank_id",
                table: "rank_badges",
                column: "rank_id");

            migrationBuilder.CreateIndex(
                name: "IX_student_badges_rank_badge_id",
                table: "student_badges",
                column: "rank_badge_id");

            migrationBuilder.CreateIndex(
                name: "IX_student_badges_student_id",
                table: "student_badges",
                column: "student_id");

            migrationBuilder.CreateIndex(
                name: "IX_student_guardian_links_created_by_user_id",
                table: "student_guardian_links",
                column: "created_by_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_student_guardian_links_parent_guardian_id",
                table: "student_guardian_links",
                column: "parent_guardian_id");

            migrationBuilder.CreateIndex(
                name: "IX_student_lesson_completions_completed_by_user_id",
                table: "student_lesson_completions",
                column: "completed_by_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_student_lesson_completions_lesson_id",
                table: "student_lesson_completions",
                column: "lesson_id");

            migrationBuilder.CreateIndex(
                name: "IX_student_lesson_completions_student_id",
                table: "student_lesson_completions",
                column: "student_id");

            migrationBuilder.CreateIndex(
                name: "IX_student_rank_progresses_rank_id",
                table: "student_rank_progresses",
                column: "rank_id");

            migrationBuilder.CreateIndex(
                name: "IX_student_rank_progresses_student_id",
                table: "student_rank_progresses",
                column: "student_id");

            migrationBuilder.CreateIndex(
                name: "IX_students_app_user_id",
                table: "students",
                column: "app_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_students_assigned_teacher_user_id",
                table: "students",
                column: "assigned_teacher_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_students_current_rank_id",
                table: "students",
                column: "current_rank_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "certificates");

            migrationBuilder.DropTable(
                name: "consent_sensitive_details");

            migrationBuilder.DropTable(
                name: "dbs_checks");

            migrationBuilder.DropTable(
                name: "lesson_change_proposals");

            migrationBuilder.DropTable(
                name: "sensitive_data_access_logs");

            migrationBuilder.DropTable(
                name: "student_badges");

            migrationBuilder.DropTable(
                name: "student_guardian_links");

            migrationBuilder.DropTable(
                name: "student_rank_progresses");

            migrationBuilder.DropTable(
                name: "user_roles");

            migrationBuilder.DropTable(
                name: "student_lesson_completions");

            migrationBuilder.DropTable(
                name: "consent_records");

            migrationBuilder.DropTable(
                name: "rank_badges");

            migrationBuilder.DropTable(
                name: "lessons");

            migrationBuilder.DropTable(
                name: "parent_guardians");

            migrationBuilder.DropTable(
                name: "students");

            migrationBuilder.DropTable(
                name: "app_users");

            migrationBuilder.DropTable(
                name: "ranks");
        }
    }
}
