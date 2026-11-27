using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddOnboardingActivation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "OnboardingAssistRequests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    PartKey = table.Column<int>(type: "integer", nullable: false),
                    PropertyId = table.Column<Guid>(type: "uuid", nullable: true),
                    Context = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    DraftWizardType = table.Column<int>(type: "integer", nullable: true),
                    DraftEntityId = table.Column<Guid>(type: "uuid", nullable: true),
                    RequestedByUserId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    RequestedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    AssignedToUserId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    StaffNote = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ActivityId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OnboardingAssistRequests", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OnboardingAssistRequests_Orgs_OrgId",
                        column: x => x.OrgId,
                        principalTable: "Orgs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OnboardingAssistRequests_Properties_PropertyId",
                        column: x => x.PropertyId,
                        principalTable: "Properties",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "OnboardingPanelSteps",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    PropertyId = table.Column<Guid>(type: "uuid", nullable: true),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    ChannelCode = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    HandoffMode = table.Column<int>(type: "integer", nullable: false),
                    HandoffNonceHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    HandoffExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CallbackConsumedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ResumeWizardType = table.Column<int>(type: "integer", nullable: true),
                    ResumeDraftEntityId = table.Column<Guid>(type: "uuid", nullable: true),
                    ResumeStepCode = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    NextVerificationAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    VerificationAttempts = table.Column<int>(type: "integer", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    VerifiedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastVerificationOutcome = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    CompletedByUserId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    ActivityId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OnboardingPanelSteps", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OnboardingPanelSteps_Orgs_OrgId",
                        column: x => x.OrgId,
                        principalTable: "Orgs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OnboardingPanelSteps_Properties_PropertyId",
                        column: x => x.PropertyId,
                        principalTable: "Properties",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "OnboardingPartStates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    PartKey = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    DeferredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RemindAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ActivityId = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedByUserId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OnboardingPartStates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OnboardingPartStates_Orgs_OrgId",
                        column: x => x.OrgId,
                        principalTable: "Orgs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "OnboardingPilotMembers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    PropertyId = table.Column<Guid>(type: "uuid", nullable: false),
                    AddedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    AddedByUserId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OnboardingPilotMembers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OnboardingPilotMembers_Orgs_OrgId",
                        column: x => x.OrgId,
                        principalTable: "Orgs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OnboardingPilotMembers_Properties_PropertyId",
                        column: x => x.PropertyId,
                        principalTable: "Properties",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PropertyFlowActivations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    PropertyId = table.Column<Guid>(type: "uuid", nullable: false),
                    Flow = table.Column<int>(type: "integer", nullable: false),
                    ActivatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ActivatedByUserId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    DeactivatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DeactivatedByUserId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PropertyFlowActivations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PropertyFlowActivations_Orgs_OrgId",
                        column: x => x.OrgId,
                        principalTable: "Orgs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PropertyFlowActivations_Properties_PropertyId",
                        column: x => x.PropertyId,
                        principalTable: "Properties",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PropertyOnboardingData",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    PropertyId = table.Column<Guid>(type: "uuid", nullable: false),
                    AlloggiatiRoomNumbersJson = table.Column<string>(type: "jsonb", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedByUserId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PropertyOnboardingData", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PropertyOnboardingData_Orgs_OrgId",
                        column: x => x.OrgId,
                        principalTable: "Orgs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PropertyOnboardingData_Properties_PropertyId",
                        column: x => x.PropertyId,
                        principalTable: "Properties",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OnboardingAssistRequests_OrgId",
                table: "OnboardingAssistRequests",
                column: "OrgId");

            migrationBuilder.CreateIndex(
                name: "IX_OnboardingAssistRequests_PropertyId",
                table: "OnboardingAssistRequests",
                column: "PropertyId");

            migrationBuilder.CreateIndex(
                name: "UIX_OnboardingAssistRequests_OpenPerPartAndProperty",
                table: "OnboardingAssistRequests",
                columns: new[] { "OrgId", "PartKey", "PropertyId" },
                unique: true,
                filter: "\"Status\" IN (1, 2)")
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "IX_OnboardingPanelSteps_PropertyId",
                table: "OnboardingPanelSteps",
                column: "PropertyId");

            migrationBuilder.CreateIndex(
                name: "UIX_OnboardingPanelSteps_OrgId_PropertyId_Kind_ChannelCode",
                table: "OnboardingPanelSteps",
                columns: new[] { "OrgId", "PropertyId", "Kind", "ChannelCode" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "UIX_OnboardingPartStates_OrgId_PartKey",
                table: "OnboardingPartStates",
                columns: new[] { "OrgId", "PartKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OnboardingPilotMembers_OrgId",
                table: "OnboardingPilotMembers",
                column: "OrgId");

            migrationBuilder.CreateIndex(
                name: "UIX_OnboardingPilotMembers_PropertyId",
                table: "OnboardingPilotMembers",
                column: "PropertyId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PropertyFlowActivations_OrgId",
                table: "PropertyFlowActivations",
                column: "OrgId");

            migrationBuilder.CreateIndex(
                name: "UIX_PropertyFlowActivations_PropertyId_Flow",
                table: "PropertyFlowActivations",
                columns: new[] { "PropertyId", "Flow" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PropertyOnboardingData_OrgId",
                table: "PropertyOnboardingData",
                column: "OrgId");

            migrationBuilder.CreateIndex(
                name: "UIX_PropertyOnboardingData_PropertyId",
                table: "PropertyOnboardingData",
                column: "PropertyId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OnboardingAssistRequests");

            migrationBuilder.DropTable(
                name: "OnboardingPanelSteps");

            migrationBuilder.DropTable(
                name: "OnboardingPartStates");

            migrationBuilder.DropTable(
                name: "OnboardingPilotMembers");

            migrationBuilder.DropTable(
                name: "PropertyFlowActivations");

            migrationBuilder.DropTable(
                name: "PropertyOnboardingData");
        }
    }
}
