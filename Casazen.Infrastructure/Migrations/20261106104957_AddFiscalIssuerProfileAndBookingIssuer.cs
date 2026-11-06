using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddFiscalIssuerProfileAndBookingIssuer : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BookingDocumentIssuers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    BookingId = table.Column<Guid>(type: "uuid", nullable: false),
                    Issuer = table.Column<int>(type: "integer", nullable: false),
                    LockedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LockedByDocumentId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BookingDocumentIssuers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BookingDocumentIssuers_Bookings_BookingId",
                        column: x => x.BookingId,
                        principalTable: "Bookings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "OrgFiscalIssuerProfiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    IssuerMode = table.Column<int>(type: "integer", nullable: false),
                    DocumentRegime = table.Column<int>(type: "integer", nullable: true),
                    LegalName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    AddressStreet = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    AddressCivic = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    PostalCode = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true),
                    City = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Province = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: true),
                    CountryCode = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: false),
                    FatturaPaRegimeCode = table.Column<string>(type: "character varying(4)", maxLength: 4, nullable: true),
                    IssuerSdiRecipientCode = table.Column<string>(type: "character varying(7)", maxLength: 7, nullable: true),
                    IssuerPec = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: true),
                    DefaultSeries = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    ConfirmedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedByUserId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrgFiscalIssuerProfiles", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "UIX_BookingDocumentIssuers_BookingId",
                table: "BookingDocumentIssuers",
                column: "BookingId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UIX_OrgFiscalIssuerProfiles_OrgId",
                table: "OrgFiscalIssuerProfiles",
                column: "OrgId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BookingDocumentIssuers");

            migrationBuilder.DropTable(
                name: "OrgFiscalIssuerProfiles");
        }
    }
}
