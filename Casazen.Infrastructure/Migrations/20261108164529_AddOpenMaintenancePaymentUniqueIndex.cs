using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddOpenMaintenancePaymentUniqueIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_BankLinkPayments_LeaseMaintenanceRequestId",
                table: "BankLinkPayments");

            migrationBuilder.CreateIndex(
                name: "UIX_BankLinkPayments_OpenMaintenanceRequest",
                table: "BankLinkPayments",
                column: "LeaseMaintenanceRequestId",
                unique: true,
                filter: "\"LeaseMaintenanceRequestId\" IS NOT NULL AND \"Status\" IN (0, 1)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UIX_BankLinkPayments_OpenMaintenanceRequest",
                table: "BankLinkPayments");

            migrationBuilder.CreateIndex(
                name: "IX_BankLinkPayments_LeaseMaintenanceRequestId",
                table: "BankLinkPayments",
                column: "LeaseMaintenanceRequestId");
        }
    }
}
