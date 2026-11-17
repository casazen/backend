using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20261117120000_AddOrgComplianceSenderDefault")]
public class AddOrgComplianceSenderDefault : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(name: "DefaultComplianceSender", table: "Orgs", type: "integer", nullable: true);
        migrationBuilder.AddColumn<DateTime>(name: "DefaultComplianceSenderChangedAt", table: "Orgs", type: "timestamp with time zone", nullable: true);
        migrationBuilder.AddColumn<string>(name: "DefaultComplianceSenderChangedByUserId", table: "Orgs", type: "character varying(255)", maxLength: 255, nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "DefaultComplianceSender", table: "Orgs");
        migrationBuilder.DropColumn(name: "DefaultComplianceSenderChangedAt", table: "Orgs");
        migrationBuilder.DropColumn(name: "DefaultComplianceSenderChangedByUserId", table: "Orgs");
    }
}
