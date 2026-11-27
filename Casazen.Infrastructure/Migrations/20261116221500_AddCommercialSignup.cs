using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations;

public partial class AddCommercialSignup : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(name: "CommercialSignupJson", table: "Orgs", type: "text", nullable: true);
        migrationBuilder.AddColumn<bool>(name: "CommercialPaymentConfirmed", table: "Orgs", type: "boolean", nullable: false, defaultValue: false);
        migrationBuilder.AddColumn<string>(name: "CommercialPaidAddonsJson", table: "Orgs", type: "text", nullable: true);
        migrationBuilder.AddColumn<int>(name: "CommercialRoomAllowance", table: "Orgs", type: "integer", nullable: false, defaultValue: 0);
    }
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "CommercialSignupJson", table: "Orgs");
        migrationBuilder.DropColumn(name: "CommercialPaymentConfirmed", table: "Orgs");
        migrationBuilder.DropColumn(name: "CommercialPaidAddonsJson", table: "Orgs");
        migrationBuilder.DropColumn(name: "CommercialRoomAllowance", table: "Orgs");
    }
}
