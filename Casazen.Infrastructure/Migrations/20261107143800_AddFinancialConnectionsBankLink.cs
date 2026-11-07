using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddFinancialConnectionsBankLink : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "FinancialConnectionsAccountId",
                table: "Orgs",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FinancialConnectionsAccountId",
                table: "LeaseContracts",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StripeCustomerId",
                table: "LeaseContracts",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "BankLinkPayments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    LeaseContractId = table.Column<Guid>(type: "uuid", nullable: true),
                    RentLedgerEntryId = table.Column<Guid>(type: "uuid", nullable: true),
                    Payer = table.Column<int>(type: "integer", nullable: false),
                    FinancialConnectionsAccountId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    StripePaymentIntentId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    StripePaymentMethodId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    PaymentMethodType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    AmountCents = table.Column<long>(type: "bigint", nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BankLinkPayments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BankLinkPayments_Orgs_OrgId",
                        column: x => x.OrgId,
                        principalTable: "Orgs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BankLinkPayments_OrgId",
                table: "BankLinkPayments",
                column: "OrgId");

            migrationBuilder.CreateIndex(
                name: "IX_BankLinkPayments_StripePaymentIntentId",
                table: "BankLinkPayments",
                column: "StripePaymentIntentId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BankLinkPayments");

            migrationBuilder.DropColumn(
                name: "FinancialConnectionsAccountId",
                table: "Orgs");

            migrationBuilder.DropColumn(
                name: "FinancialConnectionsAccountId",
                table: "LeaseContracts");

            migrationBuilder.DropColumn(
                name: "StripeCustomerId",
                table: "LeaseContracts");
        }
    }
}
