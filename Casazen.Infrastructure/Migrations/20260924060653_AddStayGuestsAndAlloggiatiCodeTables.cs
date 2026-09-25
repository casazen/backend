using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <summary>
    /// CO-12 (A5-02): the guests of a stay (<c>StayGuests</c>, one line of the Alloggiati communication each) and the
    /// official Alloggiati code tables (<c>AlloggiatiCodeEntries</c>, <c>AlloggiatiCodeTableImports</c>, empty: an admin
    /// imports them from the files of the Alloggiati portal).
    /// </summary>
    /// <remarks>
    /// Data (<see cref="BackfillStayGuestsSql"/>, idempotent): every booking gets one guest from its booker, as
    /// <c>SingleGuest</c> (1) or, when more guests are declared (<c>NumberOfGuests</c> &gt; 1), <c>HeadOfFamily</c> (2),
    /// linked to the booker's <c>Guests</c> row. Names, date of birth, citizenship, document number and place of issue
    /// are copied as text; the old free-text place of birth goes to <c>BirthComuneName</c> with <c>BornInItaly</c>
    /// unknown (the guest or the host completes it). Sex "Other" (2) and document kind "Other" (3) have no Alloggiati
    /// value: they become NULL, i.e. a missing field. Same mapping as <c>StayGuest.FromBooker</c>. Down drops the tables.
    /// Runbook: <c>docs/runbooks/alloggiati.md</c>.
    /// </remarks>
    public partial class AddStayGuestsAndAlloggiatiCodeTables : Migration
    {
        /// <summary>One guest per existing booking, from its booker (see the class remarks).</summary>
        public const string BackfillStayGuestsSql = """
            INSERT INTO "StayGuests" (
                "Id", "BookingId", "OrgId", "GuestId", "Position", "Type", "FirstName", "LastName", "Gender",
                "DateOfBirth", "BornInItaly", "BirthComuneCode", "BirthComuneName", "BirthProvince", "BirthCountryCode",
                "BirthCountryName", "CitizenshipCode", "CitizenshipName", "DocumentType", "DocumentTypeCode",
                "DocumentNumber", "DocumentIssuePlaceCode", "DocumentIssuePlaceName", "CreatedAt", "UpdatedAt")
            SELECT
                gen_random_uuid(), b."Id", b."OrgId", g."Id", 0,
                CASE WHEN b."NumberOfGuests" > 1 THEN 2 ELSE 1 END,
                left(coalesce(g."FirstName", ''), 100),
                left(coalesce(g."LastName", ''), 100),
                CASE WHEN g."Gender" IN (0, 1) THEN g."Gender" END,
                g."DateOfBirth",
                NULL, NULL, left(coalesce(g."PlaceOfBirth", ''), 100), NULL, NULL, '',
                NULL, left(coalesce(g."Nationality", ''), 100),
                CASE WHEN g."DocumentType" IN (0, 1, 2) THEN g."DocumentType" END,
                NULL, left(coalesce(g."DocumentNumber", ''), 50),
                NULL, left(coalesce(g."DocumentIssuingCountry", ''), 100),
                b."CreatedAt", b."UpdatedAt"
            FROM "Bookings" AS b
            JOIN "Guests" AS g ON g."Id" = b."GuestId"
            WHERE NOT EXISTS (SELECT 1 FROM "StayGuests" AS s WHERE s."BookingId" = b."Id");
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AlloggiatiCodeTableImports",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Table = table.Column<int>(type: "integer", nullable: false),
                    SourceFileName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    SourceVersion = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    RowCount = table.Column<int>(type: "integer", nullable: false),
                    ImportedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ImportedBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AlloggiatiCodeTableImports", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "StayGuests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BookingId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    GuestId = table.Column<Guid>(type: "uuid", nullable: true),
                    Position = table.Column<int>(type: "integer", nullable: false),
                    Type = table.Column<int>(type: "integer", nullable: false),
                    FirstName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    LastName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Gender = table.Column<int>(type: "integer", nullable: true),
                    DateOfBirth = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    BornInItaly = table.Column<bool>(type: "boolean", nullable: true),
                    BirthComuneCode = table.Column<string>(type: "character varying(9)", maxLength: 9, nullable: true),
                    BirthComuneName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    BirthProvince = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: true),
                    BirthCountryCode = table.Column<string>(type: "character varying(9)", maxLength: 9, nullable: true),
                    BirthCountryName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    CitizenshipCode = table.Column<string>(type: "character varying(9)", maxLength: 9, nullable: true),
                    CitizenshipName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    DocumentType = table.Column<int>(type: "integer", nullable: true),
                    DocumentTypeCode = table.Column<string>(type: "character varying(5)", maxLength: 5, nullable: true),
                    DocumentNumber = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    DocumentIssuePlaceCode = table.Column<string>(type: "character varying(9)", maxLength: 9, nullable: true),
                    DocumentIssuePlaceName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StayGuests", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StayGuests_Bookings_BookingId",
                        column: x => x.BookingId,
                        principalTable: "Bookings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_StayGuests_Guests_GuestId",
                        column: x => x.GuestId,
                        principalTable: "Guests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_StayGuests_Orgs_OrgId",
                        column: x => x.OrgId,
                        principalTable: "Orgs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AlloggiatiCodeEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Table = table.Column<int>(type: "integer", nullable: false),
                    Code = table.Column<string>(type: "character varying(9)", maxLength: 9, nullable: false),
                    Description = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    NormalizedDescription = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Province = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: true),
                    ImportId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AlloggiatiCodeEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AlloggiatiCodeEntries_AlloggiatiCodeTableImports_ImportId",
                        column: x => x.ImportId,
                        principalTable: "AlloggiatiCodeTableImports",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AlloggiatiCodeEntries_ImportId",
                table: "AlloggiatiCodeEntries",
                column: "ImportId");

            migrationBuilder.CreateIndex(
                name: "IX_AlloggiatiCodeEntries_Table_Code",
                table: "AlloggiatiCodeEntries",
                columns: new[] { "Table", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AlloggiatiCodeEntries_Table_NormalizedDescription",
                table: "AlloggiatiCodeEntries",
                columns: new[] { "Table", "NormalizedDescription" });

            migrationBuilder.CreateIndex(
                name: "IX_AlloggiatiCodeTableImports_Table_ImportedAt",
                table: "AlloggiatiCodeTableImports",
                columns: new[] { "Table", "ImportedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_StayGuests_BookingId_Position",
                table: "StayGuests",
                columns: new[] { "BookingId", "Position" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StayGuests_GuestId",
                table: "StayGuests",
                column: "GuestId");

            migrationBuilder.CreateIndex(
                name: "IX_StayGuests_OrgId",
                table: "StayGuests",
                column: "OrgId");

            migrationBuilder.Sql(BackfillStayGuestsSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AlloggiatiCodeEntries");

            migrationBuilder.DropTable(
                name: "StayGuests");

            migrationBuilder.DropTable(
                name: "AlloggiatiCodeTableImports");
        }
    }
}
