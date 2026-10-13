using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class LeaseMultipleParties : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Parties_LeaseContractId_Role",
                table: "Parties");

            migrationBuilder.AddColumn<int>(
                name: "Position",
                table: "Parties",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            // LT-14: existing leases keep their parties. Position in the order the detail page showed them until now
            // (role, last name), 0 for the single landlord and tenant of a lease; a lease that already had two parties
            // of one role gets 0, 1, ... so the unique index below holds. Fiscal codes normalized (upper case, no
            // spaces) like the new ones; an invalid code is kept as it is, never deleted.
            migrationBuilder.Sql("""
                UPDATE "Parties" AS p
                SET "Position" = ordered.position
                FROM (
                    SELECT "Id",
                           (row_number() OVER (PARTITION BY "LeaseContractId", "Role" ORDER BY "LastName" COLLATE "C", "Id") - 1)::integer AS position
                    FROM "Parties"
                ) AS ordered
                WHERE p."Id" = ordered."Id";

                UPDATE "Parties"
                SET "FiscalCode" = upper(regexp_replace("FiscalCode", '\s', '', 'g'))
                WHERE "FiscalCode" <> upper(regexp_replace("FiscalCode", '\s', '', 'g'));
                """);

            migrationBuilder.CreateIndex(
                name: "IX_Parties_LeaseContractId_Role_Position",
                table: "Parties",
                columns: new[] { "LeaseContractId", "Role", "Position" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Parties_LeaseContractId_Role_Position",
                table: "Parties");

            migrationBuilder.DropColumn(
                name: "Position",
                table: "Parties");

            migrationBuilder.CreateIndex(
                name: "IX_Parties_LeaseContractId_Role",
                table: "Parties",
                columns: new[] { "LeaseContractId", "Role" });
        }
    }
}
