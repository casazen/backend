using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddWizardDraft : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WizardDrafts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    WizardType = table.Column<int>(type: "integer", nullable: false),
                    EntityId = table.Column<Guid>(type: "uuid", nullable: true),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    DraftJson = table.Column<string>(type: "jsonb", nullable: false),
                    EncryptedFieldsJson = table.Column<string>(type: "text", nullable: true),
                    LastSavedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WizardDrafts", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WizardDrafts_ExpiresAt",
                table: "WizardDrafts",
                column: "ExpiresAt",
                filter: "\"UserId\" <> ''");

            migrationBuilder.CreateIndex(
                name: "UIX_WizardDrafts_OrgId_UserId_WizardType",
                table: "WizardDrafts",
                columns: new[] { "OrgId", "UserId", "WizardType" },
                unique: true,
                filter: "\"EntityId\" IS NULL AND \"UserId\" <> ''");

            migrationBuilder.CreateIndex(
                name: "UIX_WizardDrafts_OrgId_UserId_WizardType_EntityId",
                table: "WizardDrafts",
                columns: new[] { "OrgId", "UserId", "WizardType", "EntityId" },
                unique: true,
                filter: "\"EntityId\" IS NOT NULL AND \"UserId\" <> ''");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WizardDrafts");
        }
    }
}
