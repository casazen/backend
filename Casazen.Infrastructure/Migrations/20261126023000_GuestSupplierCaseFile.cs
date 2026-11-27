using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Casazen.Infrastructure.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20261126023000_GuestSupplierCaseFile")]
public sealed class GuestSupplierCaseFile : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>("CaseFileJson", "GuestSupplierOrders", "text", nullable: true);
        migrationBuilder.AddColumn<bool>("HumanReviewPending", "GuestSupplierOrders", "boolean", nullable: false, defaultValue: false);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn("HumanReviewPending", "GuestSupplierOrders");
        migrationBuilder.DropColumn("CaseFileJson", "GuestSupplierOrders");
    }
}
