using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Casazen.Infrastructure.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20261126000100_PreserveTrustedSupplierOrigin")]
public sealed class PreserveTrustedSupplierOrigin : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>("HostOrigin", "TrustedSuppliers", "boolean", nullable: true);
        migrationBuilder.AddColumn<DateTime>("RemovedAt", "TrustedSuppliers", "timestamp with time zone", nullable: true);
        migrationBuilder.Sql("""
            UPDATE "TrustedSuppliers" AS t SET "HostOrigin" = COALESCE(
                (SELECT o."HostOrigin" FROM "GuestSupplierOffers" o
                 WHERE o."HostOrgId" = t."OrgId" AND o."SupplierOrgId" = t."SupplierOrgId"
                 ORDER BY o."Id" LIMIT 1),
                CASE WHEN p."GuestPortalMarketplaceFirstPublishedAt" IS NULL THEN NOT p."GuestPortalMarketplaceEnabled"
                     ELSE t."CreatedAt" < p."GuestPortalMarketplaceFirstPublishedAt" END)
            FROM "SupplierProfiles" p WHERE p."OrgId" = t."SupplierOrgId";
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""DELETE FROM "TrustedSuppliers" WHERE "RemovedAt" IS NOT NULL;""");
        migrationBuilder.DropColumn("RemovedAt", "TrustedSuppliers");
        migrationBuilder.DropColumn("HostOrigin", "TrustedSuppliers");
    }
}
