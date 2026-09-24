using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class MakeChildEntityOrgIdsRequired : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // TN-2 pre-flight (fail loud, like MakeOrgIdRequired / MakeGuestOrgIdRequired): abort BEFORE the
            // NOT NULL + FK flip if BackfillChildEntityOrgIds left a child row without org, or with an org
            // different from its parent's (the invariant the global tenant query filter relies on).
            migrationBuilder.Sql(@"
DO $$
DECLARE
    v_null      bigint;
    v_mismatch  bigint;
    v_no_org    bigint;
BEGIN
    SELECT
        (SELECT count(*) FROM ""PropertyDocuments"" WHERE ""OrgId"" IS NULL)
      + (SELECT count(*) FROM ""OtaIntegrations"" WHERE ""OrgId"" IS NULL)
      + (SELECT count(*) FROM ""PricingAdapterConfigs"" WHERE ""OrgId"" IS NULL)
      + (SELECT count(*) FROM ""PricingHistories"" WHERE ""OrgId"" IS NULL)
      + (SELECT count(*) FROM ""AlloggiatiWebReports"" WHERE ""OrgId"" IS NULL)
    INTO v_null;

    SELECT
        (SELECT count(*) FROM ""PropertyDocuments"" c JOIN ""Properties"" p ON p.""Id"" = c.""PropertyId"" WHERE c.""OrgId"" IS DISTINCT FROM p.""OrgId"")
      + (SELECT count(*) FROM ""OtaIntegrations"" c JOIN ""Properties"" p ON p.""Id"" = c.""PropertyId"" WHERE c.""OrgId"" IS DISTINCT FROM p.""OrgId"")
      + (SELECT count(*) FROM ""PricingAdapterConfigs"" c JOIN ""Properties"" p ON p.""Id"" = c.""PropertyId"" WHERE c.""OrgId"" IS DISTINCT FROM p.""OrgId"")
      + (SELECT count(*) FROM ""PricingHistories"" c JOIN ""Properties"" p ON p.""Id"" = c.""PropertyId"" WHERE c.""OrgId"" IS DISTINCT FROM p.""OrgId"")
      + (SELECT count(*) FROM ""AlloggiatiWebReports"" c JOIN ""Bookings"" b ON b.""Id"" = c.""BookingId"" WHERE c.""OrgId"" IS DISTINCT FROM b.""OrgId"")
      + (SELECT count(*) FROM ""GuestCheckInSessions"" c JOIN ""Bookings"" b ON b.""Id"" = c.""BookingId"" WHERE c.""OrgId"" IS DISTINCT FROM b.""OrgId"")
    INTO v_mismatch;

    SELECT count(*) INTO v_no_org
    FROM ""GuestCheckInSessions"" s
    WHERE NOT EXISTS (SELECT 1 FROM ""Orgs"" o WHERE o.""Id"" = s.""OrgId"");

    IF v_null > 0 OR v_mismatch > 0 OR v_no_org > 0 THEN
        RAISE EXCEPTION 'Pre-flight failed: child_rows_without_org=%, child_rows_with_other_org_than_parent=%, checkin_sessions_with_unknown_org=%. Re-run BackfillChildEntityOrgIds before MakeChildEntityOrgIdsRequired.',
            v_null, v_mismatch, v_no_org;
    END IF;
END $$;
");

            migrationBuilder.AlterColumn<Guid>(
                name: "OrgId",
                table: "PropertyDocuments",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "OrgId",
                table: "PricingHistories",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "OrgId",
                table: "PricingAdapterConfigs",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "OrgId",
                table: "OtaIntegrations",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "OrgId",
                table: "AlloggiatiWebReports",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_GuestCheckInSessions_OrgId",
                table: "GuestCheckInSessions",
                column: "OrgId");

            migrationBuilder.AddForeignKey(
                name: "FK_AlloggiatiWebReports_Orgs_OrgId",
                table: "AlloggiatiWebReports",
                column: "OrgId",
                principalTable: "Orgs",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_GuestCheckInSessions_Orgs_OrgId",
                table: "GuestCheckInSessions",
                column: "OrgId",
                principalTable: "Orgs",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_OtaIntegrations_Orgs_OrgId",
                table: "OtaIntegrations",
                column: "OrgId",
                principalTable: "Orgs",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_PricingAdapterConfigs_Orgs_OrgId",
                table: "PricingAdapterConfigs",
                column: "OrgId",
                principalTable: "Orgs",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_PricingHistories_Orgs_OrgId",
                table: "PricingHistories",
                column: "OrgId",
                principalTable: "Orgs",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_PropertyDocuments_Orgs_OrgId",
                table: "PropertyDocuments",
                column: "OrgId",
                principalTable: "Orgs",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AlloggiatiWebReports_Orgs_OrgId",
                table: "AlloggiatiWebReports");

            migrationBuilder.DropForeignKey(
                name: "FK_GuestCheckInSessions_Orgs_OrgId",
                table: "GuestCheckInSessions");

            migrationBuilder.DropForeignKey(
                name: "FK_OtaIntegrations_Orgs_OrgId",
                table: "OtaIntegrations");

            migrationBuilder.DropForeignKey(
                name: "FK_PricingAdapterConfigs_Orgs_OrgId",
                table: "PricingAdapterConfigs");

            migrationBuilder.DropForeignKey(
                name: "FK_PricingHistories_Orgs_OrgId",
                table: "PricingHistories");

            migrationBuilder.DropForeignKey(
                name: "FK_PropertyDocuments_Orgs_OrgId",
                table: "PropertyDocuments");

            migrationBuilder.DropIndex(
                name: "IX_GuestCheckInSessions_OrgId",
                table: "GuestCheckInSessions");

            migrationBuilder.AlterColumn<Guid>(
                name: "OrgId",
                table: "PropertyDocuments",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AlterColumn<Guid>(
                name: "OrgId",
                table: "PricingHistories",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AlterColumn<Guid>(
                name: "OrgId",
                table: "PricingAdapterConfigs",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AlterColumn<Guid>(
                name: "OrgId",
                table: "OtaIntegrations",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AlterColumn<Guid>(
                name: "OrgId",
                table: "AlloggiatiWebReports",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");
        }
    }
}
