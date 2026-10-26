using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vlms.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddLessonSuggestionsAndVersions : Migration
    {
        /// <summary>
        /// The backfill of version 1 (kind 0, Converted) for every lesson, with its tags and pictures. Each lesson gets
        /// exactly one version here, so the version takes the lesson's id, and each picture keeps its own id. Checked by
        /// MigrationsTests.
        /// </summary>
        public const string BackfillVersionOneSql = """
            INSERT INTO lesson_versions (id, lesson_id, number, kind, title, "order", is_active, body, content_version, published_at)
            SELECT id, id, 1, 0, title, "order", is_active, body, content_version, now() FROM lessons;
            INSERT INTO lesson_version_tags (lesson_version_id, tag)
            SELECT lesson_id, tag FROM lesson_tag_assignments;
            INSERT INTO lesson_version_pictures (id, lesson_version_id, "order", file_name, blob_key)
            SELECT id, lesson_id, "order", file_name, blob_key FROM lesson_pictures;
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "lesson_change_proposals");

            migrationBuilder.CreateTable(
                name: "lesson_publishing_setting_changes",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false),
                    direct_publishing_allowed = table.Column<bool>(type: "boolean", nullable: false),
                    changed_by_user_id = table.Column<int>(type: "integer", nullable: false),
                    changed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_lesson_publishing_setting_changes", x => x.id);
                    table.ForeignKey(
                        name: "FK_lesson_publishing_setting_changes_app_users_changed_by_user~",
                        column: x => x.changed_by_user_id,
                        principalTable: "app_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "lesson_suggestions",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false),
                    lesson_id = table.Column<int>(type: "integer", nullable: true),
                    rank_id = table.Column<int>(type: "integer", nullable: false),
                    kind = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<int>(type: "integer", nullable: false),
                    base_version_number = table.Column<int>(type: "integer", nullable: true),
                    author_user_id = table.Column<int>(type: "integer", nullable: false),
                    title = table.Column<string>(type: "text", nullable: false),
                    order = table.Column<int>(type: "integer", nullable: false),
                    body = table.Column<string>(type: "text", nullable: false),
                    reason = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    submitted_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    decided_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    decided_by_user_id = table.Column<int>(type: "integer", nullable: true),
                    sent_back_comment = table.Column<string>(type: "text", nullable: true),
                    revises_suggestion_id = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_lesson_suggestions", x => x.id);
                    table.ForeignKey(
                        name: "FK_lesson_suggestions_app_users_author_user_id",
                        column: x => x.author_user_id,
                        principalTable: "app_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_lesson_suggestions_app_users_decided_by_user_id",
                        column: x => x.decided_by_user_id,
                        principalTable: "app_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_lesson_suggestions_lesson_suggestions_revises_suggestion_id",
                        column: x => x.revises_suggestion_id,
                        principalTable: "lesson_suggestions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_lesson_suggestions_lessons_lesson_id",
                        column: x => x.lesson_id,
                        principalTable: "lessons",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_lesson_suggestions_ranks_rank_id",
                        column: x => x.rank_id,
                        principalTable: "ranks",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "lesson_suggestion_pictures",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false),
                    lesson_suggestion_id = table.Column<int>(type: "integer", nullable: false),
                    file_name = table.Column<string>(type: "text", nullable: false),
                    blob_key = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_lesson_suggestion_pictures", x => x.id);
                    table.ForeignKey(
                        name: "FK_lesson_suggestion_pictures_lesson_suggestions_lesson_sugges~",
                        column: x => x.lesson_suggestion_id,
                        principalTable: "lesson_suggestions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "lesson_suggestion_tags",
                columns: table => new
                {
                    lesson_suggestion_id = table.Column<int>(type: "integer", nullable: false),
                    tag = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_lesson_suggestion_tags", x => new { x.lesson_suggestion_id, x.tag });
                    table.ForeignKey(
                        name: "FK_lesson_suggestion_tags_lesson_suggestions_lesson_suggestion~",
                        column: x => x.lesson_suggestion_id,
                        principalTable: "lesson_suggestions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "lesson_versions",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false),
                    lesson_id = table.Column<int>(type: "integer", nullable: false),
                    number = table.Column<int>(type: "integer", nullable: false),
                    kind = table.Column<int>(type: "integer", nullable: false),
                    title = table.Column<string>(type: "text", nullable: false),
                    order = table.Column<int>(type: "integer", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    body = table.Column<string>(type: "text", nullable: true),
                    content_version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    suggested_by_user_id = table.Column<int>(type: "integer", nullable: true),
                    suggested_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    approved_by_user_id = table.Column<int>(type: "integer", nullable: true),
                    published_by_user_id = table.Column<int>(type: "integer", nullable: true),
                    reason = table.Column<string>(type: "text", nullable: true),
                    published_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    suggestion_id = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_lesson_versions", x => x.id);
                    table.ForeignKey(
                        name: "FK_lesson_versions_app_users_approved_by_user_id",
                        column: x => x.approved_by_user_id,
                        principalTable: "app_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_lesson_versions_app_users_published_by_user_id",
                        column: x => x.published_by_user_id,
                        principalTable: "app_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_lesson_versions_app_users_suggested_by_user_id",
                        column: x => x.suggested_by_user_id,
                        principalTable: "app_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_lesson_versions_lesson_suggestions_suggestion_id",
                        column: x => x.suggestion_id,
                        principalTable: "lesson_suggestions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_lesson_versions_lessons_lesson_id",
                        column: x => x.lesson_id,
                        principalTable: "lessons",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "lesson_version_pictures",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false),
                    lesson_version_id = table.Column<int>(type: "integer", nullable: false),
                    order = table.Column<int>(type: "integer", nullable: false),
                    file_name = table.Column<string>(type: "text", nullable: false),
                    blob_key = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_lesson_version_pictures", x => x.id);
                    table.ForeignKey(
                        name: "FK_lesson_version_pictures_lesson_versions_lesson_version_id",
                        column: x => x.lesson_version_id,
                        principalTable: "lesson_versions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "lesson_version_tags",
                columns: table => new
                {
                    lesson_version_id = table.Column<int>(type: "integer", nullable: false),
                    tag = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_lesson_version_tags", x => new { x.lesson_version_id, x.tag });
                    table.ForeignKey(
                        name: "FK_lesson_version_tags_lesson_versions_lesson_version_id",
                        column: x => x.lesson_version_id,
                        principalTable: "lesson_versions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_lesson_publishing_setting_changes_changed_by_user_id",
                table: "lesson_publishing_setting_changes",
                column: "changed_by_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_lesson_suggestion_pictures_lesson_suggestion_id_file_name",
                table: "lesson_suggestion_pictures",
                columns: new[] { "lesson_suggestion_id", "file_name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_lesson_suggestions_author_user_id",
                table: "lesson_suggestions",
                column: "author_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_lesson_suggestions_decided_by_user_id",
                table: "lesson_suggestions",
                column: "decided_by_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_lesson_suggestions_lesson_id_open",
                table: "lesson_suggestions",
                column: "lesson_id",
                unique: true,
                filter: "status IN (0, 1)");

            migrationBuilder.CreateIndex(
                name: "IX_lesson_suggestions_rank_id",
                table: "lesson_suggestions",
                column: "rank_id");

            migrationBuilder.CreateIndex(
                name: "IX_lesson_suggestions_revises_suggestion_id",
                table: "lesson_suggestions",
                column: "revises_suggestion_id");

            migrationBuilder.CreateIndex(
                name: "IX_lesson_suggestions_status",
                table: "lesson_suggestions",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "IX_lesson_version_pictures_lesson_version_id_file_name",
                table: "lesson_version_pictures",
                columns: new[] { "lesson_version_id", "file_name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_lesson_versions_approved_by_user_id",
                table: "lesson_versions",
                column: "approved_by_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_lesson_versions_lesson_id_number",
                table: "lesson_versions",
                columns: new[] { "lesson_id", "number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_lesson_versions_published_by_user_id",
                table: "lesson_versions",
                column: "published_by_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_lesson_versions_suggested_by_user_id",
                table: "lesson_versions",
                column: "suggested_by_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_lesson_versions_suggestion_id",
                table: "lesson_versions",
                column: "suggestion_id");

            // Version 1 of every existing lesson, as converted (docs/adr/0013 decisions 4 and 9): its current title,
            // order, tags, text and pictures. The old proposals are not migrated (decision 7: the app is not released).
            migrationBuilder.Sql(BackfillVersionOneSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "lesson_publishing_setting_changes");

            migrationBuilder.DropTable(
                name: "lesson_suggestion_pictures");

            migrationBuilder.DropTable(
                name: "lesson_suggestion_tags");

            migrationBuilder.DropTable(
                name: "lesson_version_pictures");

            migrationBuilder.DropTable(
                name: "lesson_version_tags");

            migrationBuilder.DropTable(
                name: "lesson_versions");

            migrationBuilder.DropTable(
                name: "lesson_suggestions");

            migrationBuilder.CreateTable(
                name: "lesson_change_proposals",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false),
                    approver_user_id = table.Column<int>(type: "integer", nullable: true),
                    lesson_id = table.Column<int>(type: "integer", nullable: true),
                    proposed_by_user_id = table.Column<int>(type: "integer", nullable: false),
                    resubmission_of_proposal_id = table.Column<int>(type: "integer", nullable: true),
                    approval_comments = table.Column<string>(type: "text", nullable: true),
                    change_type = table.Column<int>(type: "integer", nullable: false),
                    decided_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    proposed_content = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<int>(type: "integer", nullable: false),
                    submitted_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
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
        }
    }
}
