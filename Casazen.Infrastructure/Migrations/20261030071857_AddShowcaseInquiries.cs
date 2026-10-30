using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddShowcaseInquiries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Kind",
                table: "ServiceRequests",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "ServiceRequestMessages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ServiceRequestId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    Author = table.Column<int>(type: "integer", nullable: false),
                    BodyEncrypted = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ReadAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ServiceRequestMessages", x => x.Id);
                    table.CheckConstraint("CK_ServiceRequestMessages_Author", "\"Author\" IN (0, 1, 2)");
                    table.ForeignKey(
                        name: "FK_ServiceRequestMessages_ServiceRequests_ServiceRequestId",
                        column: x => x.ServiceRequestId,
                        principalTable: "ServiceRequests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ShowcaseInquiries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClientRequestId = table.Column<Guid>(type: "uuid", nullable: false),
                    TokenHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    EmailHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    PayloadEncrypted = table.Column<string>(type: "text", nullable: true),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ConsumedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ServiceRequestId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ShowcaseInquiries", x => x.Id);
                    table.CheckConstraint("CK_ShowcaseInquiries_Expiry", "\"ExpiresAt\" > \"CreatedAt\"");
                    table.ForeignKey(
                        name: "FK_ShowcaseInquiries_ServiceRequests_ServiceRequestId",
                        column: x => x.ServiceRequestId,
                        principalTable: "ServiceRequests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_ShowcaseInquiries_SupplierProfiles_OrgId",
                        column: x => x.OrgId,
                        principalTable: "SupplierProfiles",
                        principalColumn: "OrgId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_ServiceRequests_Kind",
                table: "ServiceRequests",
                sql: "\"Kind\" = 0 OR (\"Kind\" = 1 AND \"RentalContext\" = 2)");

            migrationBuilder.CreateIndex(
                name: "IX_ServiceRequestMessages_OrgId_ServiceRequestId_CreatedAt",
                table: "ServiceRequestMessages",
                columns: new[] { "OrgId", "ServiceRequestId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ServiceRequestMessages_ServiceRequestId",
                table: "ServiceRequestMessages",
                column: "ServiceRequestId");

            migrationBuilder.CreateIndex(
                name: "IX_ShowcaseInquiries_ExpiresAt",
                table: "ShowcaseInquiries",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_ShowcaseInquiries_OrgId_EmailHash",
                table: "ShowcaseInquiries",
                columns: new[] { "OrgId", "EmailHash" });

            migrationBuilder.CreateIndex(
                name: "IX_ShowcaseInquiries_ServiceRequestId",
                table: "ShowcaseInquiries",
                column: "ServiceRequestId");

            migrationBuilder.CreateIndex(
                name: "UIX_ShowcaseInquiries_OrgId_ClientRequestId",
                table: "ShowcaseInquiries",
                columns: new[] { "OrgId", "ClientRequestId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ServiceRequestMessages");

            migrationBuilder.DropTable(
                name: "ShowcaseInquiries");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ServiceRequests_Kind",
                table: "ServiceRequests");

            migrationBuilder.DropColumn(
                name: "Kind",
                table: "ServiceRequests");
        }
    }
}
