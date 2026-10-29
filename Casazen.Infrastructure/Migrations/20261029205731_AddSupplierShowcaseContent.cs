using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSupplierShowcaseContent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SupplierShowcaseContents",
                columns: table => new
                {
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    SchemaVersion = table.Column<int>(type: "integer", nullable: false),
                    DraftJson = table.Column<string>(type: "jsonb", nullable: false),
                    PublishedJson = table.Column<string>(type: "jsonb", nullable: true),
                    PreviousPublishedJson = table.Column<string>(type: "jsonb", nullable: true),
                    PreviousPublishedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UndoUntil = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    SavedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    PublishedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    PausedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SupplierShowcaseContents", x => x.OrgId);
                    table.ForeignKey(
                        name: "FK_SupplierShowcaseContents_SupplierProfiles_OrgId",
                        column: x => x.OrgId,
                        principalTable: "SupplierProfiles",
                        principalColumn: "OrgId",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SupplierShowcaseContents");
        }
    }
}
