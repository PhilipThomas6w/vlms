using System;
using Microsoft.EntityFrameworkCore.Migrations;

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
                name: "AppUsers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false),
                    EntraObjectId = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Email = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppUsers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ParentGuardians",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ContactInfo = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    IsPrimary = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ParentGuardians", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Ranks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false),
                    Order = table.Column<int>(type: "int", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Ranks", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SensitiveDataAccessLogs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UserId = table.Column<int>(type: "int", nullable: true),
                    Entity = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    EntityId = table.Column<int>(type: "int", nullable: false),
                    AccessedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    AccessType = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SensitiveDataAccessLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DbsChecks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false),
                    TeacherUserId = table.Column<int>(type: "int", nullable: false),
                    CheckDate = table.Column<DateOnly>(type: "date", nullable: false),
                    ExpiryDate = table.Column<DateOnly>(type: "date", nullable: false),
                    CertificateNumber = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DbsChecks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DbsChecks_AppUsers_TeacherUserId",
                        column: x => x.TeacherUserId,
                        principalTable: "AppUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "UserRoles",
                columns: table => new
                {
                    UserId = table.Column<int>(type: "int", nullable: false),
                    Role = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserRoles", x => new { x.UserId, x.Role });
                    table.ForeignKey(
                        name: "FK_UserRoles_AppUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AppUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Lessons",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false),
                    RankId = table.Column<int>(type: "int", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Title = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ContentBlobKey = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Lessons", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Lessons_Ranks_RankId",
                        column: x => x.RankId,
                        principalTable: "Ranks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RankBadges",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false),
                    RankId = table.Column<int>(type: "int", nullable: false),
                    ImageBlobKey = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RankBadges", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RankBadges_Ranks_RankId",
                        column: x => x.RankId,
                        principalTable: "Ranks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Students",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    DateOfBirth = table.Column<DateOnly>(type: "date", nullable: false),
                    CurrentRankId = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    EnrolmentDate = table.Column<DateOnly>(type: "date", nullable: false),
                    AssignedTeacherUserId = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Students", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Students_AppUsers_AssignedTeacherUserId",
                        column: x => x.AssignedTeacherUserId,
                        principalTable: "AppUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Students_Ranks_CurrentRankId",
                        column: x => x.CurrentRankId,
                        principalTable: "Ranks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "LessonChangeProposals",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false),
                    LessonId = table.Column<int>(type: "int", nullable: true),
                    ProposedByUserId = table.Column<int>(type: "int", nullable: false),
                    ChangeType = table.Column<int>(type: "int", nullable: false),
                    ProposedContent = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    ApproverUserId = table.Column<int>(type: "int", nullable: true),
                    ApprovalComments = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    SubmittedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    DecidedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ResubmissionOfProposalId = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LessonChangeProposals", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LessonChangeProposals_AppUsers_ApproverUserId",
                        column: x => x.ApproverUserId,
                        principalTable: "AppUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LessonChangeProposals_AppUsers_ProposedByUserId",
                        column: x => x.ProposedByUserId,
                        principalTable: "AppUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LessonChangeProposals_LessonChangeProposals_ResubmissionOfProposalId",
                        column: x => x.ResubmissionOfProposalId,
                        principalTable: "LessonChangeProposals",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LessonChangeProposals_Lessons_LessonId",
                        column: x => x.LessonId,
                        principalTable: "Lessons",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ConsentRecords",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false),
                    StudentId = table.Column<int>(type: "int", nullable: false),
                    PeriodStart = table.Column<DateOnly>(type: "date", nullable: false),
                    PeriodEnd = table.Column<DateOnly>(type: "date", nullable: false),
                    PhotoMediaConsent = table.Column<bool>(type: "bit", nullable: false),
                    TransportOffsiteConsent = table.Column<bool>(type: "bit", nullable: false),
                    DataSharingConsent = table.Column<bool>(type: "bit", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    SubmittedByParentId = table.Column<int>(type: "int", nullable: false),
                    ApprovedByUserId = table.Column<int>(type: "int", nullable: true),
                    ExpiryDate = table.Column<DateOnly>(type: "date", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConsentRecords", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ConsentRecords_AppUsers_ApprovedByUserId",
                        column: x => x.ApprovedByUserId,
                        principalTable: "AppUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ConsentRecords_ParentGuardians_SubmittedByParentId",
                        column: x => x.SubmittedByParentId,
                        principalTable: "ParentGuardians",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ConsentRecords_Students_StudentId",
                        column: x => x.StudentId,
                        principalTable: "Students",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "StudentBadges",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false),
                    StudentId = table.Column<int>(type: "int", nullable: false),
                    RankBadgeId = table.Column<int>(type: "int", nullable: false),
                    AwardedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StudentBadges", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StudentBadges_RankBadges_RankBadgeId",
                        column: x => x.RankBadgeId,
                        principalTable: "RankBadges",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StudentBadges_Students_StudentId",
                        column: x => x.StudentId,
                        principalTable: "Students",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "StudentGuardianLinks",
                columns: table => new
                {
                    StudentId = table.Column<int>(type: "int", nullable: false),
                    ParentGuardianId = table.Column<int>(type: "int", nullable: false),
                    CreatedByUserId = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StudentGuardianLinks", x => new { x.StudentId, x.ParentGuardianId });
                    table.ForeignKey(
                        name: "FK_StudentGuardianLinks_AppUsers_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "AppUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StudentGuardianLinks_ParentGuardians_ParentGuardianId",
                        column: x => x.ParentGuardianId,
                        principalTable: "ParentGuardians",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_StudentGuardianLinks_Students_StudentId",
                        column: x => x.StudentId,
                        principalTable: "Students",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "StudentLessonCompletions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false),
                    StudentId = table.Column<int>(type: "int", nullable: false),
                    LessonId = table.Column<int>(type: "int", nullable: false),
                    CompletedByUserId = table.Column<int>(type: "int", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Note = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    IsReversed = table.Column<bool>(type: "bit", nullable: false),
                    ReversedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StudentLessonCompletions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StudentLessonCompletions_AppUsers_CompletedByUserId",
                        column: x => x.CompletedByUserId,
                        principalTable: "AppUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StudentLessonCompletions_Lessons_LessonId",
                        column: x => x.LessonId,
                        principalTable: "Lessons",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StudentLessonCompletions_Students_StudentId",
                        column: x => x.StudentId,
                        principalTable: "Students",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "StudentRankProgresses",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false),
                    StudentId = table.Column<int>(type: "int", nullable: false),
                    RankId = table.Column<int>(type: "int", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StudentRankProgresses", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StudentRankProgresses_Ranks_RankId",
                        column: x => x.RankId,
                        principalTable: "Ranks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StudentRankProgresses_Students_StudentId",
                        column: x => x.StudentId,
                        principalTable: "Students",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ConsentSensitiveDetails",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false),
                    ConsentRecordId = table.Column<int>(type: "int", nullable: false),
                    EmergencyMedicalInfo = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    DietarySEN = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    EmergencyContact = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConsentSensitiveDetails", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ConsentSensitiveDetails_ConsentRecords_ConsentRecordId",
                        column: x => x.ConsentRecordId,
                        principalTable: "ConsentRecords",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Certificates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false),
                    StudentLessonCompletionId = table.Column<int>(type: "int", nullable: false),
                    GeneratedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    BlobKey = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Certificates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Certificates_StudentLessonCompletions_StudentLessonCompletionId",
                        column: x => x.StudentLessonCompletionId,
                        principalTable: "StudentLessonCompletions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Certificates_StudentLessonCompletionId",
                table: "Certificates",
                column: "StudentLessonCompletionId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ConsentRecords_ApprovedByUserId",
                table: "ConsentRecords",
                column: "ApprovedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_ConsentRecords_StudentId",
                table: "ConsentRecords",
                column: "StudentId");

            migrationBuilder.CreateIndex(
                name: "IX_ConsentRecords_SubmittedByParentId",
                table: "ConsentRecords",
                column: "SubmittedByParentId");

            migrationBuilder.CreateIndex(
                name: "IX_ConsentSensitiveDetails_ConsentRecordId",
                table: "ConsentSensitiveDetails",
                column: "ConsentRecordId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DbsChecks_TeacherUserId",
                table: "DbsChecks",
                column: "TeacherUserId");

            migrationBuilder.CreateIndex(
                name: "IX_LessonChangeProposals_ApproverUserId",
                table: "LessonChangeProposals",
                column: "ApproverUserId");

            migrationBuilder.CreateIndex(
                name: "IX_LessonChangeProposals_LessonId",
                table: "LessonChangeProposals",
                column: "LessonId");

            migrationBuilder.CreateIndex(
                name: "IX_LessonChangeProposals_ProposedByUserId",
                table: "LessonChangeProposals",
                column: "ProposedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_LessonChangeProposals_ResubmissionOfProposalId",
                table: "LessonChangeProposals",
                column: "ResubmissionOfProposalId");

            migrationBuilder.CreateIndex(
                name: "IX_Lessons_RankId",
                table: "Lessons",
                column: "RankId");

            migrationBuilder.CreateIndex(
                name: "IX_RankBadges_RankId",
                table: "RankBadges",
                column: "RankId");

            migrationBuilder.CreateIndex(
                name: "IX_StudentBadges_RankBadgeId",
                table: "StudentBadges",
                column: "RankBadgeId");

            migrationBuilder.CreateIndex(
                name: "IX_StudentBadges_StudentId",
                table: "StudentBadges",
                column: "StudentId");

            migrationBuilder.CreateIndex(
                name: "IX_StudentGuardianLinks_CreatedByUserId",
                table: "StudentGuardianLinks",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_StudentGuardianLinks_ParentGuardianId",
                table: "StudentGuardianLinks",
                column: "ParentGuardianId");

            migrationBuilder.CreateIndex(
                name: "IX_StudentLessonCompletions_CompletedByUserId",
                table: "StudentLessonCompletions",
                column: "CompletedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_StudentLessonCompletions_LessonId",
                table: "StudentLessonCompletions",
                column: "LessonId");

            migrationBuilder.CreateIndex(
                name: "IX_StudentLessonCompletions_StudentId",
                table: "StudentLessonCompletions",
                column: "StudentId");

            migrationBuilder.CreateIndex(
                name: "IX_StudentRankProgresses_RankId",
                table: "StudentRankProgresses",
                column: "RankId");

            migrationBuilder.CreateIndex(
                name: "IX_StudentRankProgresses_StudentId",
                table: "StudentRankProgresses",
                column: "StudentId");

            migrationBuilder.CreateIndex(
                name: "IX_Students_AssignedTeacherUserId",
                table: "Students",
                column: "AssignedTeacherUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Students_CurrentRankId",
                table: "Students",
                column: "CurrentRankId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Certificates");

            migrationBuilder.DropTable(
                name: "ConsentSensitiveDetails");

            migrationBuilder.DropTable(
                name: "DbsChecks");

            migrationBuilder.DropTable(
                name: "LessonChangeProposals");

            migrationBuilder.DropTable(
                name: "SensitiveDataAccessLogs");

            migrationBuilder.DropTable(
                name: "StudentBadges");

            migrationBuilder.DropTable(
                name: "StudentGuardianLinks");

            migrationBuilder.DropTable(
                name: "StudentRankProgresses");

            migrationBuilder.DropTable(
                name: "UserRoles");

            migrationBuilder.DropTable(
                name: "StudentLessonCompletions");

            migrationBuilder.DropTable(
                name: "ConsentRecords");

            migrationBuilder.DropTable(
                name: "RankBadges");

            migrationBuilder.DropTable(
                name: "Lessons");

            migrationBuilder.DropTable(
                name: "ParentGuardians");

            migrationBuilder.DropTable(
                name: "Students");

            migrationBuilder.DropTable(
                name: "AppUsers");

            migrationBuilder.DropTable(
                name: "Ranks");
        }
    }
}
