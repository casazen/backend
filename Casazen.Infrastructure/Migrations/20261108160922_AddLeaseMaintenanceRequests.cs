using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddLeaseMaintenanceRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "LeaseMaintenanceRequestId",
                table: "BankLinkPayments",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "LeaseMaintenanceRequests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    LeaseContractId = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Description = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    Urgency = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    RejectionReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    AmountCents = table.Column<long>(type: "bigint", nullable: true),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    BankLinkPaymentId = table.Column<Guid>(type: "uuid", nullable: true),
                    PhotosJson = table.Column<string>(type: "text", nullable: false),
                    CreatedByUserId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    DecidedByUserId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DecidedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    PaidAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LeaseMaintenanceRequests", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LeaseMaintenanceRequests_LeaseContracts_LeaseContractId",
                        column: x => x.LeaseContractId,
                        principalTable: "LeaseContracts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_LeaseMaintenanceRequests_Orgs_OrgId",
                        column: x => x.OrgId,
                        principalTable: "Orgs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BankLinkPayments_LeaseMaintenanceRequestId",
                table: "BankLinkPayments",
                column: "LeaseMaintenanceRequestId");

            migrationBuilder.CreateIndex(
                name: "IX_LeaseMaintenanceRequests_LeaseContractId",
                table: "LeaseMaintenanceRequests",
                column: "LeaseContractId");

            migrationBuilder.CreateIndex(
                name: "IX_LeaseMaintenanceRequests_OrgId_LeaseContractId_CreatedAt",
                table: "LeaseMaintenanceRequests",
                columns: new[] { "OrgId", "LeaseContractId", "CreatedAt" });

            migrationBuilder.AddForeignKey(
                name: "FK_BankLinkPayments_LeaseMaintenanceRequests_LeaseMaintenanceR~",
                table: "BankLinkPayments",
                column: "LeaseMaintenanceRequestId",
                principalTable: "LeaseMaintenanceRequests",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_BankLinkPayments_LeaseMaintenanceRequests_LeaseMaintenanceR~",
                table: "BankLinkPayments");

            migrationBuilder.DropTable(
                name: "LeaseMaintenanceRequests");

            migrationBuilder.DropIndex(
                name: "IX_BankLinkPayments_LeaseMaintenanceRequestId",
                table: "BankLinkPayments");

            migrationBuilder.DropColumn(
                name: "LeaseMaintenanceRequestId",
                table: "BankLinkPayments");
        }
    }
}
