using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <summary>
    /// SE-01 (A8-04, A8-05, A8-06, R-09): the public sees only the revision an admin approved
    /// (<c>SeoContentPages.PublishedRevisionId</c>), each revision says whether it holds publishable text
    /// (<c>ContentStatus</c>), and approvals and withdrawals are audited (<c>SeoContentReviewEvents</c>).
    /// </summary>
    /// <remarks>
    /// Data step, after the schema: revisions holding the placeholder sentence of the stub provider become
    /// <c>Placeholder</c>, revisions without text <c>EmptyOutput</c>; every page published (<c>LegalReviewStatus</c> 1)
    /// with such a latest revision, or with none, goes back to draft with a <c>Withdrawn</c> audit row by
    /// <see cref="MigrationActor"/> that says why; the other published pages keep their latest revision (what the public
    /// saw) as published revision. Down removes the new columns and table: withdrawn pages are not published again.
    /// Runbook: <c>docs/runbooks/seo-domain.md</c> section 7.
    /// </remarks>
    public partial class SeoRevisionReview : Migration
    {
        /// <summary>Actor of the audit rows written by this migration.</summary>
        public const string MigrationActor = "system:SE-01";

        /// <summary>Marks the stub and empty revisions. Public so that a PostgreSQL test can read the rules.</summary>
        public const string MarkNotPublishableRevisionsSql = """
            UPDATE "SeoContentRevisions"
            SET "ContentStatus" = 'Placeholder'
            WHERE "BodyHtml" LIKE '%: contenuto generato per affitti brevi, CIN e tassa di soggiorno.%';

            UPDATE "SeoContentRevisions"
            SET "ContentStatus" = 'EmptyOutput'
            WHERE "ContentStatus" = 'Generated'
              AND regexp_replace(regexp_replace("BodyHtml", '<[^>]*>', '', 'g'), '(\s|&nbsp;)+', '', 'g') = '';
            """;

        /// <summary>Withdraws the published pages whose served text is not publishable (or missing), with an audit row each.</summary>
        public const string WithdrawNotPublishablePagesSql = """
            WITH latest AS (
                SELECT DISTINCT ON (r."PageId") r."PageId", r."Id", r."ContentStatus"
                FROM "SeoContentRevisions" AS r
                ORDER BY r."PageId", r."GeneratedAt" DESC, r."Id" DESC
            ),
            withdrawn AS (
                SELECT p."Id" AS "PageId", l."Id" AS "RevisionId", l."ContentStatus"
                FROM "SeoContentPages" AS p
                LEFT JOIN latest AS l ON l."PageId" = p."Id"
                WHERE p."LegalReviewStatus" = 1
                  AND (l."Id" IS NULL OR l."ContentStatus" <> 'Generated')
            ),
            audit AS (
                INSERT INTO "SeoContentReviewEvents"
                    ("Id", "PageId", "RevisionId", "Action", "ActorUserId", "OccurredAt", "CounselApproved", "Note")
                SELECT gen_random_uuid(), w."PageId", w."RevisionId", 'Withdrawn', 'system:SE-01', now(), false,
                       CASE
                           WHEN w."RevisionId" IS NULL
                               THEN 'Ritirata dalla migrazione SE-01: pagina pubblicata senza alcun testo.'
                           WHEN w."ContentStatus" = 'Placeholder'
                               THEN 'Ritirata dalla migrazione SE-01: testo segnaposto del provider Stub, mai revisionato.'
                           ELSE 'Ritirata dalla migrazione SE-01: pagina pubblicata con testo vuoto.'
                       END
                FROM withdrawn AS w
            )
            UPDATE "SeoContentPages" AS p
            SET "LegalReviewStatus" = 0, "PublishedAt" = NULL, "PublishedRevisionId" = NULL, "UpdatedAt" = now()
            FROM withdrawn AS w
            WHERE p."Id" = w."PageId";
            """;

        /// <summary>The pages still published keep, as published revision, the latest one: the text the public saw.</summary>
        public const string BackfillPublishedRevisionSql = """
            UPDATE "SeoContentPages" AS p
            SET "PublishedRevisionId" = l."Id"
            FROM (
                SELECT DISTINCT ON (r."PageId") r."PageId", r."Id"
                FROM "SeoContentRevisions" AS r
                ORDER BY r."PageId", r."GeneratedAt" DESC, r."Id" DESC
            ) AS l
            WHERE p."Id" = l."PageId" AND p."LegalReviewStatus" = 1;
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SeoContentRevisions_PageId",
                table: "SeoContentRevisions");

            migrationBuilder.AddColumn<string>(
                name: "ContentStatus",
                table: "SeoContentRevisions",
                type: "character varying(40)",
                maxLength: 40,
                nullable: false,
                defaultValue: "Generated");

            migrationBuilder.AddColumn<string>(
                name: "PromptVersion",
                table: "SeoContentRevisions",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "PublishedRevisionId",
                table: "SeoContentPages",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "SeoContentReviewEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PageId = table.Column<Guid>(type: "uuid", nullable: false),
                    RevisionId = table.Column<Guid>(type: "uuid", nullable: true),
                    Action = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ActorUserId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    OccurredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CounselApproved = table.Column<bool>(type: "boolean", nullable: false),
                    Note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SeoContentReviewEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SeoContentReviewEvents_SeoContentPages_PageId",
                        column: x => x.PageId,
                        principalTable: "SeoContentPages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SeoContentReviewEvents_SeoContentRevisions_RevisionId",
                        column: x => x.RevisionId,
                        principalTable: "SeoContentRevisions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SeoContentRevisions_PageId_GeneratedAt",
                table: "SeoContentRevisions",
                columns: new[] { "PageId", "GeneratedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_SeoContentPages_PublishedRevisionId",
                table: "SeoContentPages",
                column: "PublishedRevisionId");

            migrationBuilder.CreateIndex(
                name: "IX_SeoContentReviewEvents_PageId_OccurredAt",
                table: "SeoContentReviewEvents",
                columns: new[] { "PageId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_SeoContentReviewEvents_RevisionId",
                table: "SeoContentReviewEvents",
                column: "RevisionId");

            migrationBuilder.AddForeignKey(
                name: "FK_SeoContentPages_SeoContentRevisions_PublishedRevisionId",
                table: "SeoContentPages",
                column: "PublishedRevisionId",
                principalTable: "SeoContentRevisions",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.Sql(MarkNotPublishableRevisionsSql);
            migrationBuilder.Sql(WithdrawNotPublishablePagesSql);
            migrationBuilder.Sql(BackfillPublishedRevisionSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_SeoContentPages_SeoContentRevisions_PublishedRevisionId",
                table: "SeoContentPages");

            migrationBuilder.DropTable(
                name: "SeoContentReviewEvents");

            migrationBuilder.DropIndex(
                name: "IX_SeoContentRevisions_PageId_GeneratedAt",
                table: "SeoContentRevisions");

            migrationBuilder.DropIndex(
                name: "IX_SeoContentPages_PublishedRevisionId",
                table: "SeoContentPages");

            migrationBuilder.DropColumn(
                name: "ContentStatus",
                table: "SeoContentRevisions");

            migrationBuilder.DropColumn(
                name: "PromptVersion",
                table: "SeoContentRevisions");

            migrationBuilder.DropColumn(
                name: "PublishedRevisionId",
                table: "SeoContentPages");

            migrationBuilder.CreateIndex(
                name: "IX_SeoContentRevisions_PageId",
                table: "SeoContentRevisions",
                column: "PageId");
        }
    }
}
