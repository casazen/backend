using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Casazen.Infrastructure.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20261125221000_AddGuestHostExtras")]
public sealed class AddGuestHostExtras : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE TABLE "GuestHostExtras" (
                "Id" uuid PRIMARY KEY, "OrgId" uuid NOT NULL, "PropertyIds" uuid[] NOT NULL,
                "Name" varchar(120) NOT NULL, "Description" varchar(2000) NOT NULL,
                "Price" numeric(12,2) NOT NULL, "Pricing" integer NOT NULL, "Inventory" integer,
                "CutoffHours" integer NOT NULL, "DuringStay" boolean NOT NULL,
                "RequiresApproval" boolean NOT NULL, "Calendar" integer NOT NULL
            );
            CREATE INDEX "IX_GuestHostExtras_OrgId" ON "GuestHostExtras" ("OrgId");
            CREATE TABLE "GuestHostExtraOrders" (
                "Id" uuid PRIMARY KEY, "OrgId" uuid NOT NULL, "BookingId" uuid NOT NULL,
                "ExtraId" uuid NOT NULL, "Name" varchar(120) NOT NULL, "Quantity" integer NOT NULL,
                "Amount" numeric(12,2) NOT NULL, "Currency" varchar(3) NOT NULL, "Status" integer NOT NULL,
                "CreatedAt" timestamptz NOT NULL, "StripeIntentId" varchar(120), "StripeAccountId" varchar(120)
            );
            CREATE INDEX "IX_GuestHostExtraOrders_OrgId_BookingId" ON "GuestHostExtraOrders" ("OrgId", "BookingId");
            CREATE INDEX "IX_GuestHostExtraOrders_OrgId_ExtraId" ON "GuestHostExtraOrders" ("OrgId", "ExtraId");
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable("GuestHostExtraOrders");
        migrationBuilder.DropTable("GuestHostExtras");
    }
}
