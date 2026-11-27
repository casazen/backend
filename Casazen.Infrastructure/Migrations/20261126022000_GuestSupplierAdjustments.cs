using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Casazen.Infrastructure.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20261126022000_GuestSupplierAdjustments")]
public sealed class GuestSupplierAdjustments : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>("BankDisputeStatus", "GuestSupplierOrders", "character varying(32)", maxLength: 32, nullable: true);
        migrationBuilder.AddColumn<string>("BankDisputesJson", "GuestSupplierOrders", "text", nullable: true);
        migrationBuilder.AddColumn<int>("BankLossCents", "GuestSupplierOrders", "integer", nullable: false, defaultValue: 0);
        migrationBuilder.AddColumn<int>("RefundRequestedCents", "GuestSupplierOrders", "integer", nullable: false, defaultValue: 0);
        migrationBuilder.AddColumn<string>("RefundStatus", "GuestSupplierOrders", "character varying(32)", maxLength: 32, nullable: true);
        migrationBuilder.AddColumn<Guid>("StripeRefundAttempt", "GuestSupplierOrders", "uuid", nullable: true);
        migrationBuilder.AddColumn<DateTime>("StripeRefundAttemptedAt", "GuestSupplierOrders", "timestamp with time zone", nullable: true);
        migrationBuilder.AddColumn<string>("StripeRefundId", "GuestSupplierOrders", "character varying(255)", maxLength: 255, nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn("BankDisputeStatus", "GuestSupplierOrders");
        migrationBuilder.DropColumn("BankDisputesJson", "GuestSupplierOrders");
        migrationBuilder.DropColumn("BankLossCents", "GuestSupplierOrders");
        migrationBuilder.DropColumn("RefundRequestedCents", "GuestSupplierOrders");
        migrationBuilder.DropColumn("RefundStatus", "GuestSupplierOrders");
        migrationBuilder.DropColumn("StripeRefundAttempt", "GuestSupplierOrders");
        migrationBuilder.DropColumn("StripeRefundAttemptedAt", "GuestSupplierOrders");
        migrationBuilder.DropColumn("StripeRefundId", "GuestSupplierOrders");
    }
}
