using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddRentRegisterAndReminders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "LastReminderAt",
                table: "RentLedgerEntries",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ReminderCount",
                table: "RentLedgerEntries",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_RentLedgerEntries_OrgId_DueDate",
                table: "RentLedgerEntries",
                columns: new[] { "OrgId", "DueDate" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_RentLedgerEntries_OrgId_DueDate",
                table: "RentLedgerEntries");

            migrationBuilder.DropColumn(
                name: "LastReminderAt",
                table: "RentLedgerEntries");

            migrationBuilder.DropColumn(
                name: "ReminderCount",
                table: "RentLedgerEntries");
        }
    }
}
