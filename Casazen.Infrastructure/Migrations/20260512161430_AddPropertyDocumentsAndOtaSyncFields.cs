using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPropertyDocumentsAndOtaSyncFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LastSyncError",
                table: "OtaIntegrations",
                type: "nvarchar(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SyncStatus",
                table: "OtaIntegrations",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "PropertyDocuments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PropertyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DocumentType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    FileName = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    FileUrl = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PropertyDocuments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PropertyDocuments_Properties_PropertyId",
                        column: x => x.PropertyId,
                        principalTable: "Properties",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PropertyDocuments_PropertyId",
                table: "PropertyDocuments",
                column: "PropertyId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PropertyDocuments");

            migrationBuilder.DropColumn(
                name: "LastSyncError",
                table: "OtaIntegrations");

            migrationBuilder.DropColumn(
                name: "SyncStatus",
                table: "OtaIntegrations");
        }
    }
}
