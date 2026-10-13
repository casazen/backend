using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <summary>
    /// CO-07 (A5-21): the safety checklist of D.L. 145/2023 art. 13-ter (<c>PropertySafetyChecklists</c>, one per
    /// property, and <c>PropertySafetyChecklistItems</c>, one row per item) replaces the JSON column
    /// <c>Properties.SafetyChecklistJson</c> (smoke detector, extinguisher, gas certificate).
    /// </summary>
    /// <remarks>
    /// Data (<see cref="ImportLegacyChecklistsSql"/>, idempotent), for every property with an old checklist: a checklist
    /// with schema 1 (imported, to review), the old JSON kept verbatim in <c>LegacyChecklistJson</c> (nothing is lost)
    /// and one row per item. The old answers go to the right item: extinguisher yes becomes <c>FireExtinguishers</c>
    /// "to review" (number and floors were never asked); gas certificate yes becomes <c>SystemsCompliance</c> "to
    /// review" (it was the conformity of the gas system, not the gas and CO detectors, which were never asked); smoke
    /// detector yes stays <c>SmokeDetector</c> present (recommended, not required). A "no" is left unanswered: the old
    /// form could not tell "no" from "not answered". The old acknowledgment is not carried over: the host confirms the
    /// new checklist (SC-08). A malformed JSON gives an empty imported checklist with its text kept. Down puts the old
    /// JSON back and drops the new tables (answers given after this migration are lost).
    /// Source: <c>.claude/context/regulations/sicurezza.md</c>, «Proposta di checklist per CO-07», principle 5.
    /// </remarks>
    public partial class AddDl145SafetyChecklist : Migration
    {
        /// <summary>Imports the old JSON checklists (see the class remarks). Codes and answers are the C# enum values.</summary>
        public const string ImportLegacyChecklistsSql = """
            DO $$
            DECLARE
                r record;
                j jsonb;
                checklist_id uuid;
                now_utc timestamp with time zone := now();
            BEGIN
                FOR r IN
                    SELECT "Id", "OrgId", "SafetyChecklistJson"
                    FROM "Properties"
                    WHERE "SafetyChecklistJson" IS NOT NULL AND btrim("SafetyChecklistJson") <> ''
                LOOP
                    BEGIN
                        j := r."SafetyChecklistJson"::jsonb;
                    EXCEPTION WHEN others THEN
                        j := NULL;
                    END;
                    IF j IS NOT NULL AND jsonb_typeof(j) <> 'object' THEN
                        j := NULL;
                    END IF;

                    checklist_id := gen_random_uuid();
                    INSERT INTO "PropertySafetyChecklists" (
                        "Id", "OrgId", "PropertyId", "SchemaVersion", "LegalBasis", "LegacyChecklistJson",
                        "CreatedAt", "UpdatedAt")
                    VALUES (
                        checklist_id, r."OrgId", r."Id", 1, 'CasaZen checklist before CO-07', r."SafetyChecklistJson",
                        now_utc, now_utc)
                    ON CONFLICT ("PropertyId") DO NOTHING;
                    IF NOT FOUND THEN
                        CONTINUE;
                    END IF;

                    -- Codes: 1 FireExtinguishers, 2 GasDetector, 3 CoDetector, 4 SystemsCompliance, 5 BdsrDeclaration,
                    -- 6 SmokeDetector, 7 EmergencyInstructions. Answers: 1 Present, 3 ToReview.
                    INSERT INTO "PropertySafetyChecklistItems" ("Id", "OrgId", "ChecklistId", "Code", "Answer", "UpdatedAt")
                    SELECT gen_random_uuid(), r."OrgId", checklist_id, c.code,
                        CASE
                            WHEN c.code = 1 AND j -> 'fireExtinguisher' = 'true'::jsonb THEN 3
                            WHEN c.code = 4 AND j -> 'gasCompliance' = 'true'::jsonb THEN 3
                            WHEN c.code = 6 AND j -> 'smokeDetector' = 'true'::jsonb THEN 1
                        END,
                        now_utc
                    FROM (VALUES (1), (2), (3), (4), (5), (6), (7)) AS c(code);
                END LOOP;
            END $$;
            """;

        /// <summary>Down: the old JSON back on the property, when there was one.</summary>
        public const string RestoreLegacyChecklistsSql = """
            UPDATE "Properties" AS p
            SET "SafetyChecklistJson" = c."LegacyChecklistJson"
            FROM "PropertySafetyChecklists" AS c
            WHERE c."PropertyId" = p."Id" AND c."LegacyChecklistJson" IS NOT NULL;
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PropertySafetyChecklists",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    PropertyId = table.Column<Guid>(type: "uuid", nullable: false),
                    SchemaVersion = table.Column<int>(type: "integer", nullable: false),
                    LegalBasis = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Entrepreneurial = table.Column<bool>(type: "boolean", nullable: true),
                    HasGasSupply = table.Column<bool>(type: "boolean", nullable: true),
                    CombustionAppliances = table.Column<int[]>(type: "integer[]", nullable: true),
                    FloorCount = table.Column<int>(type: "integer", nullable: true),
                    FloorAreasSqm = table.Column<List<decimal>>(type: "numeric[]", nullable: true),
                    ConfirmedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ConfirmedBy = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    ConfirmedTextVersion = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    LegacyChecklistJson = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedBy = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PropertySafetyChecklists", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PropertySafetyChecklists_Orgs_OrgId",
                        column: x => x.OrgId,
                        principalTable: "Orgs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PropertySafetyChecklists_Properties_PropertyId",
                        column: x => x.PropertyId,
                        principalTable: "Properties",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PropertySafetyChecklistItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    ChecklistId = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<int>(type: "integer", nullable: false),
                    Answer = table.Column<int>(type: "integer", nullable: true),
                    Quantity = table.Column<int>(type: "integer", nullable: true),
                    Location = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    DetectorType = table.Column<int>(type: "integer", nullable: true),
                    CheckedOn = table.Column<DateOnly>(type: "date", nullable: true),
                    ExpiresOn = table.Column<DateOnly>(type: "date", nullable: true),
                    EvidenceDocumentId = table.Column<Guid>(type: "uuid", nullable: true),
                    Notes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedBy = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PropertySafetyChecklistItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PropertySafetyChecklistItems_Orgs_OrgId",
                        column: x => x.OrgId,
                        principalTable: "Orgs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PropertySafetyChecklistItems_PropertyDocuments_EvidenceDocu~",
                        column: x => x.EvidenceDocumentId,
                        principalTable: "PropertyDocuments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_PropertySafetyChecklistItems_PropertySafetyChecklists_Check~",
                        column: x => x.ChecklistId,
                        principalTable: "PropertySafetyChecklists",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PropertySafetyChecklistItems_ChecklistId_Code",
                table: "PropertySafetyChecklistItems",
                columns: new[] { "ChecklistId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PropertySafetyChecklistItems_EvidenceDocumentId",
                table: "PropertySafetyChecklistItems",
                column: "EvidenceDocumentId");

            migrationBuilder.CreateIndex(
                name: "IX_PropertySafetyChecklistItems_OrgId",
                table: "PropertySafetyChecklistItems",
                column: "OrgId");

            migrationBuilder.CreateIndex(
                name: "IX_PropertySafetyChecklists_OrgId",
                table: "PropertySafetyChecklists",
                column: "OrgId");

            migrationBuilder.CreateIndex(
                name: "IX_PropertySafetyChecklists_PropertyId",
                table: "PropertySafetyChecklists",
                column: "PropertyId",
                unique: true);

            migrationBuilder.Sql(ImportLegacyChecklistsSql);

            migrationBuilder.DropColumn(
                name: "SafetyChecklistJson",
                table: "Properties");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SafetyChecklistJson",
                table: "Properties",
                type: "text",
                nullable: true);

            migrationBuilder.Sql(RestoreLegacyChecklistsSql);

            migrationBuilder.DropTable(
                name: "PropertySafetyChecklistItems");

            migrationBuilder.DropTable(
                name: "PropertySafetyChecklists");
        }
    }
}
