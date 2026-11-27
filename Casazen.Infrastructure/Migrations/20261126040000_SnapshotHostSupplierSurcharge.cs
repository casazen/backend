using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Casazen.Infrastructure.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20261126040000_SnapshotHostSupplierSurcharge")]
public sealed class SnapshotHostSupplierSurcharge : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<decimal>("HostSurchargePercent", "ServiceRequests", "numeric", nullable: true);
        migrationBuilder.AddColumn<bool>("CommissionAddedToPrice", "ServiceRequestPayments", "boolean", nullable: false, defaultValue: false);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn("HostSurchargePercent", "ServiceRequests");
        migrationBuilder.DropColumn("CommissionAddedToPrice", "ServiceRequestPayments");
    }
}
