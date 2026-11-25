using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Casazen.Infrastructure.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20261125235501_AddGuestSupplierMarketplaceParticipation")]
public sealed class AddGuestSupplierMarketplaceParticipation : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>("GuestPortalMarketplaceEnabled", "SupplierProfiles",
            "boolean", nullable: false, defaultValue: false);
        migrationBuilder.AddColumn<DateTime>("GuestPortalMarketplaceFirstPublishedAt", "SupplierProfiles",
            "timestamp with time zone", nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn("GuestPortalMarketplaceFirstPublishedAt", "SupplierProfiles");
        migrationBuilder.DropColumn("GuestPortalMarketplaceEnabled", "SupplierProfiles");
    }
}
