using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Casazen.Infrastructure.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20261126004000_GuestSupplierStripeSandbox")]
public sealed class GuestSupplierStripeSandbox : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>("StripeSandbox", "GuestSupplierOrders", "boolean", nullable: false, defaultValue: false);
        migrationBuilder.AddColumn<DateTime>("StripeCheckoutStartedAt", "GuestSupplierOrders", "timestamp with time zone", nullable: true);
        migrationBuilder.AddColumn<string>("StripePaymentIntentId", "GuestSupplierOrders", "character varying(255)", maxLength: 255, nullable: true);
        migrationBuilder.CreateIndex("IX_GuestSupplierOrders_StripePaymentIntentId", "GuestSupplierOrders", "StripePaymentIntentId", unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex("IX_GuestSupplierOrders_StripePaymentIntentId", "GuestSupplierOrders");
        migrationBuilder.DropColumn("StripePaymentIntentId", "GuestSupplierOrders");
        migrationBuilder.DropColumn("StripeCheckoutStartedAt", "GuestSupplierOrders");
        migrationBuilder.DropColumn("StripeSandbox", "GuestSupplierOrders");
    }
}
