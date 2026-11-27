using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Casazen.Infrastructure.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20261126021000_GuestSupplierDeferredCheckout")]
public sealed class GuestSupplierDeferredCheckout : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>("StripeDeferred", "GuestSupplierOrders", "boolean", nullable: false, defaultValue: false);
        migrationBuilder.AddColumn<DateTime>("ExpectedSupplierConfirmationAt", "GuestSupplierOrders", "timestamp with time zone", nullable: true);
        migrationBuilder.AddColumn<DateTime>("StripeCaptureBefore", "GuestSupplierOrders", "timestamp with time zone", nullable: true);
        migrationBuilder.AddColumn<DateTime>("StripeSetupConsentAt", "GuestSupplierOrders", "timestamp with time zone", nullable: true);
        migrationBuilder.AddColumn<string>("StripeSetupConsentVersion", "GuestSupplierOrders", "character varying(64)", maxLength: 64, nullable: true);
        migrationBuilder.AddColumn<string>("StripeSetupIntentId", "GuestSupplierOrders", "character varying(255)", maxLength: 255, nullable: true);
        migrationBuilder.AddColumn<string>("StripeCustomerId", "GuestSupplierOrders", "character varying(255)", maxLength: 255, nullable: true);
        migrationBuilder.AddColumn<string>("StripeExpiredHoldIntentId", "GuestSupplierOrders", "character varying(255)", maxLength: 255, nullable: true);
        migrationBuilder.CreateIndex("IX_GuestSupplierOrders_StripeSetupIntentId", "GuestSupplierOrders", "StripeSetupIntentId", unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex("IX_GuestSupplierOrders_StripeSetupIntentId", "GuestSupplierOrders");
        migrationBuilder.DropColumn("StripeDeferred", "GuestSupplierOrders");
        migrationBuilder.DropColumn("ExpectedSupplierConfirmationAt", "GuestSupplierOrders");
        migrationBuilder.DropColumn("StripeCaptureBefore", "GuestSupplierOrders");
        migrationBuilder.DropColumn("StripeSetupConsentAt", "GuestSupplierOrders");
        migrationBuilder.DropColumn("StripeSetupConsentVersion", "GuestSupplierOrders");
        migrationBuilder.DropColumn("StripeSetupIntentId", "GuestSupplierOrders");
        migrationBuilder.DropColumn("StripeCustomerId", "GuestSupplierOrders");
        migrationBuilder.DropColumn("StripeExpiredHoldIntentId", "GuestSupplierOrders");
    }
}
