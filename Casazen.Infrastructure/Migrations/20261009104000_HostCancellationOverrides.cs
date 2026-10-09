using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations;

/// <summary>Host-customizable cancellation percentages, periods and refund type (PO 2026-10-08). Long stay 28+ unchanged.</summary>
[DbContext(typeof(AppDbContext))]
[Migration("20261009104000_HostCancellationOverrides")]
public partial class HostCancellationOverrides : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(
            name: "CancellationFullRefundHours",
            table: "Properties",
            type: "integer",
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "CancellationPartialRefundHours",
            table: "Properties",
            type: "integer",
            nullable: true);

        migrationBuilder.AddColumn<decimal>(
            name: "CancellationPartialRefundPercent",
            table: "Properties",
            type: "numeric(18,2)",
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "CancellationRefundType",
            table: "Properties",
            type: "integer",
            nullable: false,
            defaultValue: 0);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "CancellationFullRefundHours", table: "Properties");
        migrationBuilder.DropColumn(name: "CancellationPartialRefundHours", table: "Properties");
        migrationBuilder.DropColumn(name: "CancellationPartialRefundPercent", table: "Properties");
        migrationBuilder.DropColumn(name: "CancellationRefundType", table: "Properties");
    }
}
