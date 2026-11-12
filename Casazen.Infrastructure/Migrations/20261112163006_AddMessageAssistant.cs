using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddMessageAssistant : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Origin",
                table: "GuestMessages",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<string>(
                name: "RuleCode",
                table: "GuestMessages",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            // The messages that exist were written before the assistant, by the guest (Sender 0) or by a person of the host (Sender 1): the column gave every one of
            // them the guest as author (MessageOrigin.Guest = 1), so the messages of the host become a person's (MessageOrigin.Person = 2). Nothing else is rewritten.
            migrationBuilder.Sql("UPDATE \"GuestMessages\" SET \"Origin\" = 2 WHERE \"Sender\" = 1;");

            migrationBuilder.CreateTable(
                name: "AssistantDecisionLogs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    PropertyId = table.Column<Guid>(type: "uuid", nullable: true),
                    ThreadKey = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    InboundMessageId = table.Column<Guid>(type: "uuid", nullable: false),
                    Situation = table.Column<int>(type: "integer", nullable: true),
                    Urgency = table.Column<int>(type: "integer", nullable: true),
                    SensitiveCategories = table.Column<int>(type: "integer", nullable: false),
                    ConfidenceScore = table.Column<decimal>(type: "numeric(4,3)", precision: 4, scale: 3, nullable: true),
                    ScoreSituation = table.Column<decimal>(type: "numeric(4,3)", precision: 4, scale: 3, nullable: true),
                    ScoreFacts = table.Column<decimal>(type: "numeric(4,3)", precision: 4, scale: 3, nullable: true),
                    ScoreRules = table.Column<decimal>(type: "numeric(4,3)", precision: 4, scale: 3, nullable: true),
                    Decision = table.Column<int>(type: "integer", nullable: false),
                    GateFailures = table.Column<List<string>>(type: "text[]", nullable: false),
                    Model = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    ModelVersion = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    InstructionVersion = table.Column<int>(type: "integer", nullable: false),
                    SourceRefs = table.Column<string>(type: "jsonb", nullable: false),
                    ApprovalItemId = table.Column<Guid>(type: "uuid", nullable: true),
                    OutboundMessageId = table.Column<Guid>(type: "uuid", nullable: true),
                    ActivityId = table.Column<Guid>(type: "uuid", nullable: true),
                    ActivityClosedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ApproverUserId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    ApprovedWithoutEdit = table.Column<bool>(type: "boolean", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssistantDecisionLogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AssistantDecisionLogs_ApprovalQueueItems_ApprovalItemId",
                        column: x => x.ApprovalItemId,
                        principalTable: "ApprovalQueueItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_AssistantDecisionLogs_Orgs_OrgId",
                        column: x => x.OrgId,
                        principalTable: "Orgs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AssistantDecisionLogs_Properties_PropertyId",
                        column: x => x.PropertyId,
                        principalTable: "Properties",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "AssistantLessons",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    PropertyId = table.Column<Guid>(type: "uuid", nullable: true),
                    ThreadKey = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    ApprovalItemId = table.Column<Guid>(type: "uuid", nullable: true),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    Topic = table.Column<int>(type: "integer", nullable: false),
                    ProposedText = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    Scope = table.Column<int>(type: "integer", nullable: true),
                    DecidedByUserId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    DecidedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    AppliedEntryId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssistantLessons", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AssistantLessons_Orgs_OrgId",
                        column: x => x.OrgId,
                        principalTable: "Orgs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AssistantLessons_Properties_PropertyId",
                        column: x => x.PropertyId,
                        principalTable: "Properties",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AssistantSituationSettings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    PropertyId = table.Column<Guid>(type: "uuid", nullable: true),
                    Situation = table.Column<int>(type: "integer", nullable: false),
                    Mode = table.Column<int>(type: "integer", nullable: false),
                    ResponseTemplate = table.Column<string>(type: "text", nullable: true),
                    ResponseSource = table.Column<int>(type: "integer", nullable: true),
                    ModeSince = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedByUserId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssistantSituationSettings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AssistantSituationSettings_Orgs_OrgId",
                        column: x => x.OrgId,
                        principalTable: "Orgs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AssistantSituationSettings_Properties_PropertyId",
                        column: x => x.PropertyId,
                        principalTable: "Properties",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AssistantTrustProposals",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    PropertyId = table.Column<Guid>(type: "uuid", nullable: true),
                    Situation = table.Column<int>(type: "integer", nullable: false),
                    FromMode = table.Column<int>(type: "integer", nullable: false),
                    ToMode = table.Column<int>(type: "integer", nullable: false),
                    DraftsMeasured = table.Column<int>(type: "integer", nullable: false),
                    ApprovedWithoutEditPercent = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    PeriodStart = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    DecidedByUserId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    DecidedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssistantTrustProposals", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AssistantTrustProposals_Orgs_OrgId",
                        column: x => x.OrgId,
                        principalTable: "Orgs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AssistantTrustProposals_Properties_PropertyId",
                        column: x => x.PropertyId,
                        principalTable: "Properties",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "OrgAssistantRules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    Tone = table.Column<int>(type: "integer", nullable: false),
                    Languages = table.Column<List<string>>(type: "text[]", nullable: false),
                    Signature = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    HoursFrom = table.Column<TimeOnly>(type: "time without time zone", nullable: true),
                    HoursTo = table.Column<TimeOnly>(type: "time without time zone", nullable: true),
                    AllowedTopics = table.Column<List<string>>(type: "text[]", nullable: false),
                    ForbiddenTopics = table.Column<List<string>>(type: "text[]", nullable: false),
                    ConfidenceThreshold = table.Column<decimal>(type: "numeric(4,3)", precision: 4, scale: 3, nullable: false),
                    InstructionVersion = table.Column<int>(type: "integer", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedByUserId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrgAssistantRules", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OrgAssistantRules_Orgs_OrgId",
                        column: x => x.OrgId,
                        principalTable: "Orgs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PropertyAssistantSettings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    PropertyId = table.Column<Guid>(type: "uuid", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: true),
                    Tone = table.Column<int>(type: "integer", nullable: true),
                    Signature = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    HoursFrom = table.Column<TimeOnly>(type: "time without time zone", nullable: true),
                    HoursTo = table.Column<TimeOnly>(type: "time without time zone", nullable: true),
                    ConfidenceThreshold = table.Column<decimal>(type: "numeric(4,3)", precision: 4, scale: 3, nullable: true),
                    ForbiddenTopics = table.Column<List<string>>(type: "text[]", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedByUserId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PropertyAssistantSettings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PropertyAssistantSettings_Orgs_OrgId",
                        column: x => x.OrgId,
                        principalTable: "Orgs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PropertyAssistantSettings_Properties_PropertyId",
                        column: x => x.PropertyId,
                        principalTable: "Properties",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PropertyKnowledgeEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    PropertyId = table.Column<Guid>(type: "uuid", nullable: true),
                    Topic = table.Column<int>(type: "integer", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    Source = table.Column<int>(type: "integer", nullable: false),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Text = table.Column<string>(type: "text", nullable: false),
                    Language = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: true),
                    LessonId = table.Column<Guid>(type: "uuid", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedByUserId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PropertyKnowledgeEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PropertyKnowledgeEntries_Orgs_OrgId",
                        column: x => x.OrgId,
                        principalTable: "Orgs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PropertyKnowledgeEntries_Properties_PropertyId",
                        column: x => x.PropertyId,
                        principalTable: "Properties",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AssistantDecisionLogs_ApprovalItemId",
                table: "AssistantDecisionLogs",
                column: "ApprovalItemId");

            migrationBuilder.CreateIndex(
                name: "IX_AssistantDecisionLogs_OrgId_PropertyId_CreatedAt",
                table: "AssistantDecisionLogs",
                columns: new[] { "OrgId", "PropertyId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AssistantDecisionLogs_PropertyId",
                table: "AssistantDecisionLogs",
                column: "PropertyId");

            migrationBuilder.CreateIndex(
                name: "UIX_AssistantDecisionLogs_InboundMessageId",
                table: "AssistantDecisionLogs",
                column: "InboundMessageId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AssistantLessons_OrgId_Status",
                table: "AssistantLessons",
                columns: new[] { "OrgId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_AssistantLessons_PropertyId",
                table: "AssistantLessons",
                column: "PropertyId");

            migrationBuilder.CreateIndex(
                name: "IX_AssistantSituationSettings_PropertyId",
                table: "AssistantSituationSettings",
                column: "PropertyId");

            migrationBuilder.CreateIndex(
                name: "UIX_AssistantSituationSettings_OrgId_PropertyId_Situation",
                table: "AssistantSituationSettings",
                columns: new[] { "OrgId", "PropertyId", "Situation" },
                unique: true,
                filter: "\"PropertyId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "UIX_AssistantSituationSettings_OrgId_Situation",
                table: "AssistantSituationSettings",
                columns: new[] { "OrgId", "Situation" },
                unique: true,
                filter: "\"PropertyId\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_AssistantTrustProposals_OrgId_Status",
                table: "AssistantTrustProposals",
                columns: new[] { "OrgId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_AssistantTrustProposals_PropertyId",
                table: "AssistantTrustProposals",
                column: "PropertyId");

            migrationBuilder.CreateIndex(
                name: "UIX_OrgAssistantRules_OrgId",
                table: "OrgAssistantRules",
                column: "OrgId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PropertyAssistantSettings_PropertyId",
                table: "PropertyAssistantSettings",
                column: "PropertyId");

            migrationBuilder.CreateIndex(
                name: "UIX_PropertyAssistantSettings_OrgId_PropertyId",
                table: "PropertyAssistantSettings",
                columns: new[] { "OrgId", "PropertyId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PropertyKnowledgeEntries_OrgId_PropertyId_Topic",
                table: "PropertyKnowledgeEntries",
                columns: new[] { "OrgId", "PropertyId", "Topic" });

            migrationBuilder.CreateIndex(
                name: "IX_PropertyKnowledgeEntries_PropertyId",
                table: "PropertyKnowledgeEntries",
                column: "PropertyId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AssistantDecisionLogs");

            migrationBuilder.DropTable(
                name: "AssistantLessons");

            migrationBuilder.DropTable(
                name: "AssistantSituationSettings");

            migrationBuilder.DropTable(
                name: "AssistantTrustProposals");

            migrationBuilder.DropTable(
                name: "OrgAssistantRules");

            migrationBuilder.DropTable(
                name: "PropertyAssistantSettings");

            migrationBuilder.DropTable(
                name: "PropertyKnowledgeEntries");

            migrationBuilder.DropColumn(
                name: "Origin",
                table: "GuestMessages");

            migrationBuilder.DropColumn(
                name: "RuleCode",
                table: "GuestMessages");
        }
    }
}
