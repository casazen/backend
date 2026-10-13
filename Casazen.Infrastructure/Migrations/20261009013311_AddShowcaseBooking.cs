using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddShowcaseBooking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<Guid>(
                name: "PropertyId",
                table: "ServiceRequests",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<Guid>(
                name: "CustomerId",
                table: "ServiceRequests",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LocationAccessNotes",
                table: "ServiceRequests",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LocationAddress",
                table: "ServiceRequests",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LocationCity",
                table: "ServiceRequests",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LocationComuneIstat",
                table: "ServiceRequests",
                type: "character varying(6)",
                maxLength: 6,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LocationFloor",
                table: "ServiceRequests",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LocationPostalCode",
                table: "ServiceRequests",
                type: "character varying(10)",
                maxLength: 10,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PublicCode",
                table: "ServiceRequests",
                type: "character varying(10)",
                maxLength: 10,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ReminderSentAt",
                table: "ServiceRequests",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Source",
                table: "ServiceRequests",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "ServiceCustomers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    EmailHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    FullName = table.Column<string>(type: "text", nullable: false),
                    Email = table.Column<string>(type: "text", nullable: false),
                    Phone = table.Column<string>(type: "text", nullable: true),
                    Locale = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: false),
                    PrivacyNoticeVersion = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    PrivacyAcceptedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ConsentIp = table.Column<string>(type: "character varying(45)", maxLength: 45, nullable: false),
                    AnonymizedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ServiceCustomers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ServiceCustomers_SupplierProfiles_OrgId",
                        column: x => x.OrgId,
                        principalTable: "SupplierProfiles",
                        principalColumn: "OrgId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ShowcaseBookingHolds",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClientRequestId = table.Column<Guid>(type: "uuid", nullable: false),
                    StartUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EndUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    PublicCode = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    TokenHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    EmailHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    PayloadEncrypted = table.Column<string>(type: "text", nullable: true),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ConsumedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ServiceRequestId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ShowcaseBookingHolds", x => x.Id);
                    table.CheckConstraint("CK_ShowcaseBookingHolds_Expiry", "\"ExpiresAt\" > \"CreatedAt\"");
                    table.CheckConstraint("CK_ShowcaseBookingHolds_Interval", "\"StartUtc\" < \"EndUtc\"");
                    table.ForeignKey(
                        name: "FK_ShowcaseBookingHolds_ServiceRequests_ServiceRequestId",
                        column: x => x.ServiceRequestId,
                        principalTable: "ServiceRequests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_ShowcaseBookingHolds_SupplierProfiles_OrgId",
                        column: x => x.OrgId,
                        principalTable: "SupplierProfiles",
                        principalColumn: "OrgId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ServiceRequests_CustomerId",
                table: "ServiceRequests",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "UIX_ServiceRequests_SupplierOrgId_PublicCode",
                table: "ServiceRequests",
                columns: new[] { "SupplierOrgId", "PublicCode" },
                unique: true,
                filter: "\"PublicCode\" IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ServiceRequests_Context",
                table: "ServiceRequests",
                sql: "(\"RentalContext\" = 2 AND \"PropertyId\" IS NULL AND \"BookingId\" IS NULL AND \"Source\" = 1 AND \"CustomerId\" IS NOT NULL AND \"PublicCode\" IS NOT NULL AND \"LocationCity\" IS NOT NULL) OR (\"RentalContext\" IN (0, 1) AND \"PropertyId\" IS NOT NULL AND \"Source\" = 0 AND \"CustomerId\" IS NULL AND \"PublicCode\" IS NULL AND \"LocationComuneIstat\" IS NULL AND \"LocationCity\" IS NULL AND \"LocationPostalCode\" IS NULL AND \"LocationAddress\" IS NULL AND \"LocationFloor\" IS NULL AND \"LocationAccessNotes\" IS NULL)");

            migrationBuilder.CreateIndex(
                name: "UIX_ServiceCustomers_OrgId_EmailHash",
                table: "ServiceCustomers",
                columns: new[] { "OrgId", "EmailHash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ShowcaseBookingHolds_ExpiresAt",
                table: "ShowcaseBookingHolds",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_ShowcaseBookingHolds_OrgId_EmailHash",
                table: "ShowcaseBookingHolds",
                columns: new[] { "OrgId", "EmailHash" });

            migrationBuilder.CreateIndex(
                name: "IX_ShowcaseBookingHolds_OrgId_StartUtc",
                table: "ShowcaseBookingHolds",
                columns: new[] { "OrgId", "StartUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ShowcaseBookingHolds_ServiceRequestId",
                table: "ShowcaseBookingHolds",
                column: "ServiceRequestId");

            migrationBuilder.CreateIndex(
                name: "UIX_ShowcaseBookingHolds_OrgId_ClientRequestId",
                table: "ShowcaseBookingHolds",
                columns: new[] { "OrgId", "ClientRequestId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UIX_ShowcaseBookingHolds_OrgId_PublicCode",
                table: "ShowcaseBookingHolds",
                columns: new[] { "OrgId", "PublicCode" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_ServiceRequests_ServiceCustomers_CustomerId",
                table: "ServiceRequests",
                column: "CustomerId",
                principalTable: "ServiceCustomers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ServiceRequests_ServiceCustomers_CustomerId",
                table: "ServiceRequests");

            migrationBuilder.DropTable(
                name: "ServiceCustomers");

            migrationBuilder.DropTable(
                name: "ShowcaseBookingHolds");

            migrationBuilder.DropIndex(
                name: "IX_ServiceRequests_CustomerId",
                table: "ServiceRequests");

            migrationBuilder.DropIndex(
                name: "UIX_ServiceRequests_SupplierOrgId_PublicCode",
                table: "ServiceRequests");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ServiceRequests_Context",
                table: "ServiceRequests");

            migrationBuilder.DropColumn(
                name: "CustomerId",
                table: "ServiceRequests");

            migrationBuilder.DropColumn(
                name: "LocationAccessNotes",
                table: "ServiceRequests");

            migrationBuilder.DropColumn(
                name: "LocationAddress",
                table: "ServiceRequests");

            migrationBuilder.DropColumn(
                name: "LocationCity",
                table: "ServiceRequests");

            migrationBuilder.DropColumn(
                name: "LocationComuneIstat",
                table: "ServiceRequests");

            migrationBuilder.DropColumn(
                name: "LocationFloor",
                table: "ServiceRequests");

            migrationBuilder.DropColumn(
                name: "LocationPostalCode",
                table: "ServiceRequests");

            migrationBuilder.DropColumn(
                name: "PublicCode",
                table: "ServiceRequests");

            migrationBuilder.DropColumn(
                name: "ReminderSentAt",
                table: "ServiceRequests");

            migrationBuilder.DropColumn(
                name: "Source",
                table: "ServiceRequests");

            // A request from a showcase has no property: putting the column back to NOT NULL would have to invent one. The update
            // that EF generates for the default points such a row at a property that does not exist, so the foreign key refuses it
            // and the whole revert (one transaction) is undone: nothing is lost by reverting too early. Without showcase requests
            // it changes nothing.
            migrationBuilder.AlterColumn<Guid>(
                name: "PropertyId",
                table: "ServiceRequests",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            // The default was only there to carry the update above; the column had none before.
            migrationBuilder.Sql("ALTER TABLE \"ServiceRequests\" ALTER COLUMN \"PropertyId\" DROP DEFAULT;");
        }
    }
}
