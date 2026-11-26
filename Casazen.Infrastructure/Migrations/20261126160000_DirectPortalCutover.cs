using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Casazen.Infrastructure.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20261126160000_DirectPortalCutover")]
public sealed class DirectPortalCutover : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<DateOnly>("DirectAlloggiatiFrom", "PropertyComplianceSenders", "date", nullable: true);
        migrationBuilder.AddColumn<DateOnly>("DirectIstatFrom", "PropertyComplianceSenders", "date", nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn("DirectAlloggiatiFrom", "PropertyComplianceSenders");
        migrationBuilder.DropColumn("DirectIstatFrom", "PropertyComplianceSenders");
    }
}
