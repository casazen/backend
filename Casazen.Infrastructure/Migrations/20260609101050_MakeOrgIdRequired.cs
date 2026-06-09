using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class MakeOrgIdRequired : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Pre-flight NULL-OrgId guard (AC10b, fail loud): abort BEFORE the NOT-NULL flip if any
            // tenant-scoped row still lacks an OrgId. The NOT-NULL conversion must never run silently
            // against un-backfilled data. Residual rows are remediated to the 'casazen-unassigned'
            // fallback Org (Step 2) and the pre-flight re-run — never auto-flipped into a real tenant.
            migrationBuilder.Sql(@"
DO $$
DECLARE n bigint;
BEGIN
    SELECT (SELECT count(*) FROM ""Properties""     WHERE ""OrgId"" IS NULL)
         + (SELECT count(*) FROM ""Bookings""       WHERE ""OrgId"" IS NULL)
         + (SELECT count(*) FROM ""LeaseContracts"" WHERE ""OrgId"" IS NULL)
         + (SELECT count(*) FROM ""Payments""       WHERE ""OrgId"" IS NULL) INTO n;
    IF n > 0 THEN
        RAISE EXCEPTION 'Pre-flight failed: % tenant-scoped rows still have NULL OrgId. Run quarantine remediation before MakeOrgIdRequired.', n;
    END IF;
END $$;
");

            migrationBuilder.AlterColumn<Guid>(
                name: "OrgId",
                table: "Properties",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "OrgId",
                table: "Payments",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "OrgId",
                table: "LeaseContracts",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "OrgId",
                table: "Bookings",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Bookings_Orgs_OrgId",
                table: "Bookings",
                column: "OrgId",
                principalTable: "Orgs",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_LeaseContracts_Orgs_OrgId",
                table: "LeaseContracts",
                column: "OrgId",
                principalTable: "Orgs",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Payments_Orgs_OrgId",
                table: "Payments",
                column: "OrgId",
                principalTable: "Orgs",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Properties_Orgs_OrgId",
                table: "Properties",
                column: "OrgId",
                principalTable: "Orgs",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Users_Orgs_OrgId",
                table: "Users",
                column: "OrgId",
                principalTable: "Orgs",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Bookings_Orgs_OrgId",
                table: "Bookings");

            migrationBuilder.DropForeignKey(
                name: "FK_LeaseContracts_Orgs_OrgId",
                table: "LeaseContracts");

            migrationBuilder.DropForeignKey(
                name: "FK_Payments_Orgs_OrgId",
                table: "Payments");

            migrationBuilder.DropForeignKey(
                name: "FK_Properties_Orgs_OrgId",
                table: "Properties");

            migrationBuilder.DropForeignKey(
                name: "FK_Users_Orgs_OrgId",
                table: "Users");

            migrationBuilder.AlterColumn<Guid>(
                name: "OrgId",
                table: "Properties",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AlterColumn<Guid>(
                name: "OrgId",
                table: "Payments",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AlterColumn<Guid>(
                name: "OrgId",
                table: "LeaseContracts",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AlterColumn<Guid>(
                name: "OrgId",
                table: "Bookings",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");
        }
    }
}
