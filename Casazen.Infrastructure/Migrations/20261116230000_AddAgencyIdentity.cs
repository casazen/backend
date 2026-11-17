using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20261116230000_AddAgencyIdentity")]
public class AddAgencyIdentity : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>(
            name: "IsAgency", table: "Orgs", type: "boolean", nullable: false, defaultValue: false);
        migrationBuilder.CreateIndex(
            name: "IX_HostInviteRecords_UsedByUserId", table: "HostInviteRecords", column: "UsedByUserId");
        // Preserve existing agencies when billing changes later.
        migrationBuilder.Sql("UPDATE \"Orgs\" SET \"IsAgency\" = TRUE WHERE \"PlanTier\" = 3 AND \"OrgType\" = 0");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(name: "IX_HostInviteRecords_UsedByUserId", table: "HostInviteRecords");
        migrationBuilder.DropColumn(name: "IsAgency", table: "Orgs");
    }
}
