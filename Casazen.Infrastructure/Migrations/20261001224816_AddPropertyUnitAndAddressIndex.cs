using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPropertyUnitAndAddressIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Properties_Address_City_PostalCode_IsActive",
                table: "Properties");

            // PC-06 (A2-33): numeric(9,6) cannot hold a latitude or longitude that cannot exist (the old API accepted any
            // number). Reset those pairs to "not set" so that narrowing the columns can never fail the deploy; the original
            // values are not kept, runbook docs/runbooks/property-address.md says how to list them BEFORE the deploy.
            migrationBuilder.Sql(
                """
                UPDATE "Properties"
                SET "Latitude" = 0, "Longitude" = 0
                WHERE abs("Latitude") > 90 OR abs("Longitude") > 180;
                """);

            migrationBuilder.AlterColumn<decimal>(
                name: "Longitude",
                table: "Properties",
                type: "numeric(9,6)",
                precision: 9,
                scale: 6,
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(18,2)",
                oldPrecision: 18,
                oldScale: 2);

            migrationBuilder.AlterColumn<decimal>(
                name: "Latitude",
                table: "Properties",
                type: "numeric(9,6)",
                precision: 9,
                scale: 6,
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(18,2)",
                oldPrecision: 18,
                oldScale: 2);

            migrationBuilder.AddColumn<string>(
                name: "Unit",
                table: "Properties",
                type: "character varying(30)",
                maxLength: 30,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AddressKey",
                table: "Properties",
                type: "text",
                nullable: true,
                computedColumnSql: "lower(regexp_replace(btrim(\"Address\"), '\\s+', ' ', 'g')) || '|' || lower(regexp_replace(btrim(\"City\"), '\\s+', ' ', 'g')) || '|' || lower(btrim(\"PostalCode\")) || '|' || lower(regexp_replace(btrim(coalesce(\"Unit\", '')), '\\s+', ' ', 'g'))",
                stored: true);

            // PC-06 (A2-19): the old unique index was global and exact, so active properties of the SAME org whose
            // addresses differ only by case or spaces exist and would collide in the new per-org index. The oldest of each
            // group is left as it is; every later one gets a visible, distinct unit ("dup-" + 8 hex characters of its Id) so
            // the index can be created, nothing is deleted or hidden, and the rows can be found and resolved with the host
            // (runbook docs/runbooks/property-address.md). "AddressKey" is the generated column added above.
            migrationBuilder.Sql(
                """
                UPDATE "Properties" AS p
                SET "Unit" = 'dup-' || substr(replace(p."Id"::text, '-', ''), 1, 8)
                FROM (
                    SELECT "Id",
                           row_number() OVER (PARTITION BY "OrgId", "AddressKey" ORDER BY "CreatedAt", "Id") AS rn
                    FROM "Properties"
                    WHERE "IsActive" = true AND "IsDeleted" = false
                ) AS d
                WHERE p."Id" = d."Id" AND d.rn > 1;
                """);

            migrationBuilder.CreateIndex(
                name: "UIX_Properties_OrgId_AddressKey",
                table: "Properties",
                columns: new[] { "OrgId", "AddressKey" },
                unique: true,
                filter: "\"IsActive\" = true AND \"IsDeleted\" = false");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UIX_Properties_OrgId_AddressKey",
                table: "Properties");

            migrationBuilder.DropColumn(
                name: "AddressKey",
                table: "Properties");

            migrationBuilder.DropColumn(
                name: "Unit",
                table: "Properties");

            migrationBuilder.AlterColumn<decimal>(
                name: "Longitude",
                table: "Properties",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(9,6)",
                oldPrecision: 9,
                oldScale: 6);

            migrationBuilder.AlterColumn<decimal>(
                name: "Latitude",
                table: "Properties",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(9,6)",
                oldPrecision: 9,
                oldScale: 6);

            migrationBuilder.CreateIndex(
                name: "IX_Properties_Address_City_PostalCode_IsActive",
                table: "Properties",
                columns: new[] { "Address", "City", "PostalCode", "IsActive" },
                unique: true,
                filter: "\"IsActive\" = true AND \"IsDeleted\" = false");
        }
    }
}
