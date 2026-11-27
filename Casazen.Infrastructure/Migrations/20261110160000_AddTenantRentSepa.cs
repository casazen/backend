using System;
using Microsoft.EntityFrameworkCore.Migrations;
#nullable disable
namespace Casazen.Infrastructure.Migrations;

public partial class AddTenantRentSepa : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(name: "SepaSetupIntentId", table: "RentSchedules", type: "character varying(255)", maxLength: 255, nullable: true);
        migrationBuilder.AddColumn<string>(name: "SepaCustomerId", table: "RentSchedules", type: "character varying(255)", maxLength: 255, nullable: true);
        migrationBuilder.AddColumn<string>(name: "SepaPaymentMethodId", table: "RentSchedules", type: "character varying(255)", maxLength: 255, nullable: true);
        migrationBuilder.AddColumn<string>(name: "SepaAuthorizedByUserId", table: "RentSchedules", type: "character varying(200)", maxLength: 200, nullable: true);
        migrationBuilder.AddColumn<DateTime>(name: "SepaAuthorizedAt", table: "RentSchedules", type: "timestamp with time zone", nullable: true);
        migrationBuilder.AddColumn<bool>(name: "SepaAutomaticEnabled", table: "RentSchedules", type: "boolean", nullable: false, defaultValue: false);
        migrationBuilder.AddColumn<DateOnly>(name: "SepaAutomaticFrom", table: "RentSchedules", type: "date", nullable: true);
        migrationBuilder.AddColumn<int>(name: "SepaSetupGeneration", table: "RentSchedules", type: "integer", nullable: false, defaultValue: 0);
        migrationBuilder.AddColumn<decimal>(name: "TransactionFee", table: "RentLedgerEntries", type: "decimal(18,2)", precision: 18, scale: 2, nullable: false, defaultValue: 0m);
        migrationBuilder.AddColumn<DateTime>(name: "SepaAutomaticAttemptedAt", table: "RentLedgerEntries", type: "timestamp with time zone", nullable: true);
        migrationBuilder.AddColumn<DateTime>(name: "ReminderSentAt", table: "CondominiumInstallments", type: "timestamp with time zone", nullable: true);
    }
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "SepaSetupIntentId", table: "RentSchedules");
        migrationBuilder.DropColumn(name: "SepaCustomerId", table: "RentSchedules");
        migrationBuilder.DropColumn(name: "SepaPaymentMethodId", table: "RentSchedules");
        migrationBuilder.DropColumn(name: "SepaAuthorizedByUserId", table: "RentSchedules");
        migrationBuilder.DropColumn(name: "SepaAuthorizedAt", table: "RentSchedules");
        migrationBuilder.DropColumn(name: "SepaAutomaticEnabled", table: "RentSchedules");
        migrationBuilder.DropColumn(name: "SepaAutomaticFrom", table: "RentSchedules");
        migrationBuilder.DropColumn(name: "SepaSetupGeneration", table: "RentSchedules");
        migrationBuilder.DropColumn(name: "TransactionFee", table: "RentLedgerEntries");
        migrationBuilder.DropColumn(name: "SepaAutomaticAttemptedAt", table: "RentLedgerEntries");
        migrationBuilder.DropColumn(name: "ReminderSentAt", table: "CondominiumInstallments");
    }
}
