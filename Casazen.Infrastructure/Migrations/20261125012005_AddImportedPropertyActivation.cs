using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddImportedPropertyActivation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "RequiredActivationPortalsJson",
                table: "Properties",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "UsesMasterAccount",
                table: "OctorateConnections",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "OctoratePreparations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BatchId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    PropertyId = table.Column<Guid>(type: "uuid", nullable: false),
                    Environment = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    AccommodationId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    EncryptedRefreshToken = table.Column<string>(type: "text", nullable: true),
                    ProductCreationRequestedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    SupplierReference = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    ErrorCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OctoratePreparations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OctoratePreparations_Properties_PropertyId",
                        column: x => x.PropertyId,
                        principalTable: "Properties",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PropertyImportReviews",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    Provider = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    SourceId = table.Column<string>(type: "character varying(240)", maxLength: 240, nullable: false),
                    Snapshot = table.Column<string>(type: "text", nullable: false),
                    ResolvedFields = table.Column<string>(type: "text", nullable: false),
                    TodoId = table.Column<Guid>(type: "uuid", nullable: true),
                    CheckedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PropertyImportReviews", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PropertyImportReviews_Properties_Id",
                        column: x => x.Id,
                        principalTable: "Properties",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OctoratePreparations_Environment_AccommodationId",
                table: "OctoratePreparations",
                columns: new[] { "Environment", "AccommodationId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OctoratePreparations_Environment_BatchId",
                table: "OctoratePreparations",
                columns: new[] { "Environment", "BatchId" });

            migrationBuilder.CreateIndex(
                name: "IX_OctoratePreparations_Environment_PropertyId",
                table: "OctoratePreparations",
                columns: new[] { "Environment", "PropertyId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OctoratePreparations_PropertyId",
                table: "OctoratePreparations",
                column: "PropertyId");

            migrationBuilder.CreateIndex(
                name: "IX_PropertyImportReviews_OrgId_Provider_SourceId",
                table: "PropertyImportReviews",
                columns: new[] { "OrgId", "Provider", "SourceId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OctoratePreparations");

            migrationBuilder.DropTable(
                name: "PropertyImportReviews");

            migrationBuilder.DropColumn(
                name: "RequiredActivationPortalsJson",
                table: "Properties");

            migrationBuilder.DropColumn(
                name: "UsesMasterAccount",
                table: "OctorateConnections");
        }
    }
}
