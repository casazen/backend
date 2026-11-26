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
            "PropertyId" uuid NOT NULL REFERENCES "Properties" ("Id") ON DELETE CASCADE,
            "Day" date NOT NULL,
            "Hash" text NOT NULL,
            "State" text NOT NULL,
            "UpdatedAt" timestamptz NOT NULL DEFAULT now(),
            "Accepted" integer NOT NULL DEFAULT 0,
            "Rejected" integer NOT NULL DEFAULT 0,
            "Outcomes" text NOT NULL DEFAULT '[]',
            "RecordKeys" text NOT NULL,
            PRIMARY KEY ("OrgId", "PropertyId", "Day")
        );
        CREATE TABLE "Ross1000DayHistory" (
            "Id" bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
            "OrgId" uuid NOT NULL,
            "PropertyId" uuid NOT NULL REFERENCES "Properties" ("Id") ON DELETE CASCADE,
            "Day" date NOT NULL,
            "Hash" text NOT NULL,
            "State" text NOT NULL,
            "UpdatedAt" timestamptz NOT NULL,
            "Accepted" integer NOT NULL,
            "Rejected" integer NOT NULL,
            "Outcomes" text NOT NULL DEFAULT '[]',
            "RecordKeys" text NOT NULL
        );
        CREATE INDEX "IX_Ross1000DayHistory_Day" ON "Ross1000DayHistory" ("OrgId","PropertyId","Day");
        CREATE FUNCTION "CaptureRoss1000Day"() RETURNS trigger AS $$
        BEGIN
            INSERT INTO "Ross1000DayHistory" ("OrgId","PropertyId","Day","Hash","State","UpdatedAt","Accepted","Rejected","RecordKeys","Outcomes")
            VALUES (NEW."OrgId",NEW."PropertyId",NEW."Day",NEW."Hash",NEW."State",NEW."UpdatedAt",NEW."Accepted",NEW."Rejected",NEW."RecordKeys",NEW."Outcomes");
            RETURN NEW;
        END; $$ LANGUAGE plpgsql;
        CREATE TRIGGER "CaptureRoss1000Day" AFTER INSERT OR UPDATE ON "Ross1000Days"
        FOR EACH ROW EXECUTE FUNCTION "CaptureRoss1000Day"();
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        DROP TABLE "Ross1000Days";
        DROP FUNCTION "CaptureRoss1000Day"();
        DROP TABLE "Ross1000DayHistory";
        """);
}
