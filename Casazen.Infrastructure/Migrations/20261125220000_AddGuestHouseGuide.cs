using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Casazen.Infrastructure.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20261125220000_AddGuestHouseGuide")]
public sealed class AddGuestHouseGuide : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>("GuestHouseGuideJson", "Properties", "text", nullable: true);
        migrationBuilder.AddColumn<string>("AcceptedHouseRulesVersion", "GuestCheckInSessions",
            "character varying(64)", maxLength: 64, nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn("AcceptedHouseRulesVersion", "GuestCheckInSessions");
        migrationBuilder.DropColumn("GuestHouseGuideJson", "Properties");
    }
}
