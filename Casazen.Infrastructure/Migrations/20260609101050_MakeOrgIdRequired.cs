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

            // Zero-downtime NOT-NULL + FK flip (AC10b / design Migration Plan, Deploy 3). The naive
            // AlterColumn(nullable:false) emits a validating SET NOT NULL (full-table scan under
            // ACCESS EXCLUSIVE) and AddForeignKey validates immediately (SHARE ROW EXCLUSIVE), both
            // of which lock a populated Supabase table. Instead, for each of the four tenant tables:
            //   1. ADD the FK ... NOT VALID (brief lock, no scan) then VALIDATE CONSTRAINT
            //      (SHARE UPDATE EXCLUSIVE — concurrent reads/writes keep running during the scan).
            //   2. ADD a CHECK ("OrgId" IS NOT NULL) NOT VALID -> VALIDATE CONSTRAINT, then
            //      ALTER COLUMN ... SET NOT NULL (Postgres 12+ reuses the validated CHECK and skips
            //      the table scan), then DROP the now-redundant CHECK.
            // Static SQL only (no C# concatenation/interpolation); constraint names match EF
            // conventions and the model snapshot, so the resulting schema is byte-for-byte identical
            // to the generated migration — only the lock profile changes.

            migrationBuilder.Sql(@"
ALTER TABLE ""Properties"" ADD CONSTRAINT ""FK_Properties_Orgs_OrgId""
    FOREIGN KEY (""OrgId"") REFERENCES ""Orgs"" (""Id"") ON DELETE RESTRICT NOT VALID;
ALTER TABLE ""Properties"" VALIDATE CONSTRAINT ""FK_Properties_Orgs_OrgId"";
ALTER TABLE ""Properties"" ADD CONSTRAINT ""CK_Properties_OrgId_NotNull"" CHECK (""OrgId"" IS NOT NULL) NOT VALID;
ALTER TABLE ""Properties"" VALIDATE CONSTRAINT ""CK_Properties_OrgId_NotNull"";
ALTER TABLE ""Properties"" ALTER COLUMN ""OrgId"" SET NOT NULL;
ALTER TABLE ""Properties"" DROP CONSTRAINT ""CK_Properties_OrgId_NotNull"";
");

            migrationBuilder.Sql(@"
ALTER TABLE ""Payments"" ADD CONSTRAINT ""FK_Payments_Orgs_OrgId""
    FOREIGN KEY (""OrgId"") REFERENCES ""Orgs"" (""Id"") ON DELETE RESTRICT NOT VALID;
ALTER TABLE ""Payments"" VALIDATE CONSTRAINT ""FK_Payments_Orgs_OrgId"";
ALTER TABLE ""Payments"" ADD CONSTRAINT ""CK_Payments_OrgId_NotNull"" CHECK (""OrgId"" IS NOT NULL) NOT VALID;
ALTER TABLE ""Payments"" VALIDATE CONSTRAINT ""CK_Payments_OrgId_NotNull"";
ALTER TABLE ""Payments"" ALTER COLUMN ""OrgId"" SET NOT NULL;
ALTER TABLE ""Payments"" DROP CONSTRAINT ""CK_Payments_OrgId_NotNull"";
");

            migrationBuilder.Sql(@"
ALTER TABLE ""LeaseContracts"" ADD CONSTRAINT ""FK_LeaseContracts_Orgs_OrgId""
    FOREIGN KEY (""OrgId"") REFERENCES ""Orgs"" (""Id"") ON DELETE RESTRICT NOT VALID;
ALTER TABLE ""LeaseContracts"" VALIDATE CONSTRAINT ""FK_LeaseContracts_Orgs_OrgId"";
ALTER TABLE ""LeaseContracts"" ADD CONSTRAINT ""CK_LeaseContracts_OrgId_NotNull"" CHECK (""OrgId"" IS NOT NULL) NOT VALID;
ALTER TABLE ""LeaseContracts"" VALIDATE CONSTRAINT ""CK_LeaseContracts_OrgId_NotNull"";
ALTER TABLE ""LeaseContracts"" ALTER COLUMN ""OrgId"" SET NOT NULL;
ALTER TABLE ""LeaseContracts"" DROP CONSTRAINT ""CK_LeaseContracts_OrgId_NotNull"";
");

            migrationBuilder.Sql(@"
ALTER TABLE ""Bookings"" ADD CONSTRAINT ""FK_Bookings_Orgs_OrgId""
    FOREIGN KEY (""OrgId"") REFERENCES ""Orgs"" (""Id"") ON DELETE RESTRICT NOT VALID;
ALTER TABLE ""Bookings"" VALIDATE CONSTRAINT ""FK_Bookings_Orgs_OrgId"";
ALTER TABLE ""Bookings"" ADD CONSTRAINT ""CK_Bookings_OrgId_NotNull"" CHECK (""OrgId"" IS NOT NULL) NOT VALID;
ALTER TABLE ""Bookings"" VALIDATE CONSTRAINT ""CK_Bookings_OrgId_NotNull"";
ALTER TABLE ""Bookings"" ALTER COLUMN ""OrgId"" SET NOT NULL;
ALTER TABLE ""Bookings"" DROP CONSTRAINT ""CK_Bookings_OrgId_NotNull"";
");

            // Users.OrgId stays nullable (AC9) — add only the FK, lock-light (NOT VALID -> VALIDATE).
            migrationBuilder.Sql(@"
ALTER TABLE ""Users"" ADD CONSTRAINT ""FK_Users_Orgs_OrgId""
    FOREIGN KEY (""OrgId"") REFERENCES ""Orgs"" (""Id"") ON DELETE RESTRICT NOT VALID;
ALTER TABLE ""Users"" VALIDATE CONSTRAINT ""FK_Users_Orgs_OrgId"";
");
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
