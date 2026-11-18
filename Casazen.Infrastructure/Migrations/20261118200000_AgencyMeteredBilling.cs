using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Casazen.Infrastructure.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20261118200000_AgencyMeteredBilling")]
public sealed class AgencyMeteredBilling : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        CREATE TABLE "AgencyUnitActivity" (
            "Id" bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
            "OrgId" uuid NOT NULL, "PropertyId" uuid NOT NULL,
            "StartedAt" timestamptz NOT NULL, "EndedAt" timestamptz
        );
        CREATE UNIQUE INDEX "UX_AgencyUnitActivity_Open" ON "AgencyUnitActivity" ("OrgId","PropertyId") WHERE "EndedAt" IS NULL;
        CREATE INDEX "IX_AgencyUnitActivity_Month" ON "AgencyUnitActivity" ("OrgId","StartedAt","EndedAt");
        CREATE TABLE "AgencyUsageContracts" (
            "SubscriptionId" text PRIMARY KEY, "OrgId" uuid NOT NULL, "CustomerId" text NOT NULL,
            "StartedAt" timestamptz NOT NULL, "EndedAt" timestamptz, "Events" text[] NOT NULL,
            "ReportedAt" timestamptz, "ReportedUnits" integer
        );

        CREATE FUNCTION casazen_agency_unit_activity() RETURNS trigger LANGUAGE plpgsql AS $$
        DECLARE observed timestamptz := clock_timestamp();
        BEGIN
            IF TG_OP <> 'DELETE' THEN
                IF NEW."IsActive" AND NOT NEW."IsDeleted" AND NOT NEW."IsPaused" AND NOT NEW."IsTestData"
                   AND EXISTS (SELECT 1 FROM "Orgs" WHERE "Id" = NEW."OrgId" AND "IsAgency") THEN
                    IF TG_OP = 'UPDATE' AND OLD."OrgId" <> NEW."OrgId" THEN
                        UPDATE "AgencyUnitActivity" SET "EndedAt" = observed
                        WHERE "OrgId" = OLD."OrgId" AND "PropertyId" = OLD."Id" AND "EndedAt" IS NULL;
                    END IF;
                    INSERT INTO "AgencyUnitActivity" ("OrgId","PropertyId","StartedAt")
                    VALUES (NEW."OrgId",NEW."Id",observed) ON CONFLICT DO NOTHING;
                    RETURN NEW;
                END IF;
            END IF;
            IF TG_OP <> 'INSERT' THEN
                UPDATE "AgencyUnitActivity" SET "EndedAt" = observed
                WHERE "OrgId" = OLD."OrgId" AND "PropertyId" = OLD."Id" AND "EndedAt" IS NULL;
            END IF;
            RETURN NULL;
        END $$;
        CREATE TRIGGER agency_unit_activity AFTER INSERT OR DELETE OR UPDATE OF "IsActive","IsPaused","IsDeleted","IsTestData","OrgId"
        ON "Properties" FOR EACH ROW EXECUTE FUNCTION casazen_agency_unit_activity();
        INSERT INTO "AgencyUnitActivity" ("OrgId","PropertyId","StartedAt")
        SELECT p."OrgId",p."Id",statement_timestamp() FROM "Properties" p JOIN "Orgs" o ON o."Id"=p."OrgId"
        WHERE o."IsAgency" AND p."IsActive" AND NOT p."IsDeleted" AND NOT p."IsPaused" AND NOT p."IsTestData";
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        DROP TRIGGER agency_unit_activity ON "Properties";
        DROP FUNCTION casazen_agency_unit_activity();
        DROP TABLE "AgencyUsageContracts","AgencyUnitActivity";
        """);
}
