using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class MakeGuestOrgIdRequired : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // TN-1 pre-flight (fail loud, like MakeOrgIdRequired): abort BEFORE the NOT NULL + FK flip if
            // BackfillGuestOrgIds left a guest without org, or if a booking / Alloggiati report still points
            // at a guest of another org (the tenant invariant the global query filter relies on).
            migrationBuilder.Sql(@"
DO $$
DECLARE
    v_null_org      bigint;
    v_cross_booking bigint;
    v_cross_report  bigint;
BEGIN
    SELECT count(*) INTO v_null_org FROM ""Guests"" WHERE ""OrgId"" IS NULL;

    SELECT count(*) INTO v_cross_booking
    FROM ""Bookings"" b
    JOIN ""Guests"" g ON g.""Id"" = b.""GuestId""
    WHERE g.""OrgId"" IS DISTINCT FROM b.""OrgId"";

    SELECT count(*) INTO v_cross_report
    FROM ""AlloggiatiWebReports"" r
    JOIN ""Bookings"" b ON b.""Id"" = r.""BookingId""
    JOIN ""Guests"" g ON g.""Id"" = r.""GuestId""
    WHERE g.""OrgId"" IS DISTINCT FROM b.""OrgId"";

    IF v_null_org > 0 OR v_cross_booking > 0 OR v_cross_report > 0 THEN
        RAISE EXCEPTION 'Pre-flight failed: guests_without_org=%, bookings_with_other_org_guest=%, reports_with_other_org_guest=%. Re-run BackfillGuestOrgIds before MakeGuestOrgIdRequired.',
            v_null_org, v_cross_booking, v_cross_report;
    END IF;
END $$;
");

            migrationBuilder.AlterColumn<Guid>(
                name: "OrgId",
                table: "Guests",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Guests_Orgs_OrgId",
                table: "Guests",
                column: "OrgId",
                principalTable: "Orgs",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Guests_Orgs_OrgId",
                table: "Guests");

            migrationBuilder.AlterColumn<Guid>(
                name: "OrgId",
                table: "Guests",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");
        }
    }
}
