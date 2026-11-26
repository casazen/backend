using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Casazen.Infrastructure.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20261126143000_AddRoss1000Days")]
public sealed class AddRoss1000Days : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        CREATE TABLE "Ross1000Days" (
            "OrgId" uuid NOT NULL,
            "PropertyId" uuid NOT NULL REFERENCES "Properties" ("Id"),
            "Day" date NOT NULL,
            "Hash" text NOT NULL,
            "State" text NOT NULL,
            "UpdatedAt" timestamptz NOT NULL DEFAULT now(),
            "Accepted" integer NOT NULL DEFAULT 0,
            "Rejected" integer NOT NULL DEFAULT 0,
            "RecordKeys" text NOT NULL,
            PRIMARY KEY ("OrgId", "PropertyId", "Day")
        );
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.DropTable("Ross1000Days");
}
