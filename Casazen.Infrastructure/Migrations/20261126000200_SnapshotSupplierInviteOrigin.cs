using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Casazen.Infrastructure.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20261126000200_SnapshotSupplierInviteOrigin")]
public sealed class SnapshotSupplierInviteOrigin : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>("HostOriginAtInvitation", "SupplierInviteRecords", "boolean", nullable: true);
        migrationBuilder.AddColumn<Guid>("IdentifiedSupplierOrgId", "SupplierInviteRecords", "uuid", nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn("HostOriginAtInvitation", "SupplierInviteRecords");
        migrationBuilder.DropColumn("IdentifiedSupplierOrgId", "SupplierInviteRecords");
    }
}
