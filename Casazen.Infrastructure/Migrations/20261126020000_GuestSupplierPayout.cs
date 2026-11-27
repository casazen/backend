using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Casazen.Infrastructure.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20261126020000_GuestSupplierPayout")]
public sealed class GuestSupplierPayout : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>("StripePayoutChargeId", "GuestSupplierOrders", "character varying(255)", maxLength: 255, nullable: true);
        migrationBuilder.AddColumn<int>("StripeCardFeeCents", "GuestSupplierOrders", "integer", nullable: true);
        migrationBuilder.AddColumn<int>("StripeSupplierTransferCents", "GuestSupplierOrders", "integer", nullable: false, defaultValue: 0);
        migrationBuilder.AddColumn<int>("StripeHostTransferCents", "GuestSupplierOrders", "integer", nullable: false, defaultValue: 0);
        migrationBuilder.AddColumn<string>("StripeSupplierTransferId", "GuestSupplierOrders", "character varying(255)", maxLength: 255, nullable: true);
        migrationBuilder.AddColumn<string>("StripeHostTransferId", "GuestSupplierOrders", "character varying(255)", maxLength: 255, nullable: true);
        migrationBuilder.AddColumn<string>("StripeSupplierDestination", "GuestSupplierOrders", "character varying(255)", maxLength: 255, nullable: true);
        migrationBuilder.AddColumn<string>("StripeHostDestination", "GuestSupplierOrders", "character varying(255)", maxLength: 255, nullable: true);
        migrationBuilder.AddColumn<DateTime>("StripePayoutClaimedAt", "GuestSupplierOrders", "timestamp with time zone", nullable: true);
        migrationBuilder.AddColumn<DateTime>("StripeReleasedAt", "GuestSupplierOrders", "timestamp with time zone", nullable: true);
        migrationBuilder.AddColumn<DateTime>("StripePayoutAttemptAfter", "GuestSupplierOrders", "timestamp with time zone", nullable: true);
        migrationBuilder.AddColumn<string>("StripePayoutFailureCode", "GuestSupplierOrders", "character varying(255)", maxLength: 255, nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn("StripePayoutChargeId", "GuestSupplierOrders");
        migrationBuilder.DropColumn("StripeCardFeeCents", "GuestSupplierOrders");
        migrationBuilder.DropColumn("StripeSupplierTransferCents", "GuestSupplierOrders");
        migrationBuilder.DropColumn("StripeHostTransferCents", "GuestSupplierOrders");
        migrationBuilder.DropColumn("StripeSupplierTransferId", "GuestSupplierOrders");
        migrationBuilder.DropColumn("StripeHostTransferId", "GuestSupplierOrders");
        migrationBuilder.DropColumn("StripeSupplierDestination", "GuestSupplierOrders");
        migrationBuilder.DropColumn("StripeHostDestination", "GuestSupplierOrders");
        migrationBuilder.DropColumn("StripePayoutClaimedAt", "GuestSupplierOrders");
        migrationBuilder.DropColumn("StripeReleasedAt", "GuestSupplierOrders");
        migrationBuilder.DropColumn("StripePayoutAttemptAfter", "GuestSupplierOrders");
        migrationBuilder.DropColumn("StripePayoutFailureCode", "GuestSupplierOrders");
    }
}
