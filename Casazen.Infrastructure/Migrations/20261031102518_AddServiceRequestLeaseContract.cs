using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddServiceRequestLeaseContract : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "LeaseContractId",
                table: "ServiceRequests",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ServiceRequests_LeaseContractId",
                table: "ServiceRequests",
                column: "LeaseContractId",
                filter: "\"LeaseContractId\" IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ServiceRequests_LeaseContract",
                table: "ServiceRequests",
                sql: "\"LeaseContractId\" IS NULL OR \"RentalContext\" = 1");

            migrationBuilder.AddForeignKey(
                name: "FK_ServiceRequests_LeaseContracts_LeaseContractId",
                table: "ServiceRequests",
                column: "LeaseContractId",
                principalTable: "LeaseContracts",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ServiceRequests_LeaseContracts_LeaseContractId",
                table: "ServiceRequests");

            migrationBuilder.DropIndex(
                name: "IX_ServiceRequests_LeaseContractId",
                table: "ServiceRequests");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ServiceRequests_LeaseContract",
                table: "ServiceRequests");

            migrationBuilder.DropColumn(
                name: "LeaseContractId",
                table: "ServiceRequests");
        }
    }
}
