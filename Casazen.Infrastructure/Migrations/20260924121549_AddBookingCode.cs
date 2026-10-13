using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <summary>
    /// BK-11 (A3-10): <c>Bookings.BookingCode</c>, the readable booking code that the guest types with the email in
    /// "Le mie prenotazioni", unique per org. New bookings get it from the application (<c>BookingCodes.New</c>).
    /// </summary>
    /// <remarks>
    /// Existing bookings get a random code of the same form: 10 characters of the Crockford base32 alphabet, each from
    /// the low 5 bits of a byte of <c>gen_random_uuid()</c> (PostgreSQL 13+, strong random source). The bytes 6 and 8
    /// carry the UUID version and variant: only the 10 fully random bytes are used, so every character is uniform.
    /// </remarks>
    public partial class AddBookingCode : Migration
    {
        /// <summary>Backfill of the booking codes. Public so that a PostgreSQL test runs exactly this statement.</summary>
        public const string BackfillBookingCodesSql = """
            UPDATE "Bookings" AS b
            SET "BookingCode" = c.code
            FROM (
                SELECT r.id,
                       string_agg(
                           substr('0123456789ABCDEFGHJKMNPQRSTVWXYZ', (get_byte(r.bytes, p.pos) % 32) + 1, 1),
                           '' ORDER BY p.ord) AS code
                FROM (
                    SELECT "Id" AS id, uuid_send(gen_random_uuid()) AS bytes
                    FROM "Bookings"
                    WHERE "BookingCode" = ''
                ) AS r
                CROSS JOIN LATERAL unnest(ARRAY[0, 1, 2, 3, 4, 5, 7, 9, 10, 11]) WITH ORDINALITY AS p(pos, ord)
                GROUP BY r.id
            ) AS c
            WHERE b."Id" = c.id;
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BookingCode",
                table: "Bookings",
                type: "character varying(10)",
                maxLength: 10,
                nullable: false,
                defaultValue: "");

            migrationBuilder.Sql(BackfillBookingCodesSql);

            migrationBuilder.CreateIndex(
                name: "IX_Bookings_OrgId_BookingCode",
                table: "Bookings",
                columns: new[] { "OrgId", "BookingCode" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Bookings_OrgId_BookingCode",
                table: "Bookings");

            migrationBuilder.DropColumn(
                name: "BookingCode",
                table: "Bookings");
        }
    }
}
