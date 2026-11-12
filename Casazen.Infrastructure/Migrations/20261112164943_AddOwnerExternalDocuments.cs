using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddOwnerExternalDocuments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ComplianceStatus",
                table: "AgencyInvoices",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "legacy_unverified");

            migrationBuilder.CreateTable(
                name: "OwnerExternalDocumentReferences",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerStatementVersionId = table.Column<Guid>(type: "uuid", nullable: false),
                    PropertyOwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    Provider = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ExternalId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ExternalNumber = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ExternalDate = table.Column<DateOnly>(type: "date", nullable: false),
                    DocumentUrl = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    DocumentSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    VerifiedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    VerifiedByUserId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    RemovedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RemovedByUserId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    RemovalReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedByUserId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OwnerExternalDocumentReferences", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OwnerExternalDocumentReferences_OwnerStatementVersions_Owne~",
                        column: x => x.OwnerStatementVersionId,
                        principalTable: "OwnerStatementVersions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OwnerExternalDocumentReferences_PropertyOwners_PropertyOwne~",
                        column: x => x.PropertyOwnerId,
                        principalTable: "PropertyOwners",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OwnerExternalDocumentReferences_OrgId_PropertyOwnerId",
                table: "OwnerExternalDocumentReferences",
                columns: new[] { "OrgId", "PropertyOwnerId" });

            migrationBuilder.CreateIndex(
                name: "IX_OwnerExternalDocumentReferences_OwnerStatementVersionId",
                table: "OwnerExternalDocumentReferences",
                column: "OwnerStatementVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_OwnerExternalDocumentReferences_PropertyOwnerId",
                table: "OwnerExternalDocumentReferences",
                column: "PropertyOwnerId");

            migrationBuilder.CreateIndex(
                name: "UIX_OwnerExternalDocumentReferences_Live",
                table: "OwnerExternalDocumentReferences",
                columns: new[] { "OrgId", "OwnerStatementVersionId", "Provider", "ExternalId" },
                unique: true,
                filter: "\"RemovedAt\" IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OwnerExternalDocumentReferences");

            migrationBuilder.DropColumn(
                name: "ComplianceStatus",
                table: "AgencyInvoices");
        }
    }
}
