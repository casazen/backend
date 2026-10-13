using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <summary>
    /// PC-07 (A2-08, A2-30): <c>Bookings.CleaningFee</c>, the cleaning fee included in <c>BasePrice</c> when the booking was
    /// priced, so the lodging per night is <c>(BasePrice - CleaningFee) / nights</c> whatever the property's fee is today;
    /// <c>Bookings.CancellationNote</c>, the reason the host wrote when cancelling (host only).
    /// </summary>
    /// <remarks>
    /// Bookings priced by CasaZen (booking site <c>Direct</c> = 0, host <c>Manual</c> = 8) always had
    /// <c>BasePrice = NightlyRate x nights + CleaningFee</c> of their property: they get the property's current fee, capped
    /// at the base price. It is the fee they were priced with unless the host has changed it since; bookings of other
    /// sources (channels) keep 0.
    /// </remarks>
    public partial class AddBookingCleaningFeeAndCancellationNote : Migration
    {
        /// <summary>Backfill of the cleaning fee. Public so that a PostgreSQL test runs exactly this statement.</summary>
        public const string BackfillCleaningFeeSql = """
            UPDATE "Bookings" AS b
            SET "CleaningFee" = LEAST(p."CleaningFee", b."BasePrice")
            FROM "Properties" AS p
            WHERE p."Id" = b."PropertyId"
              AND b."Source" IN (0, 8)
              AND b."BasePrice" > 0
              AND p."CleaningFee" > 0;
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CancellationNote",
                table: "Bookings",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "CleaningFee",
                table: "Bookings",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.Sql(BackfillCleaningFeeSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CancellationNote",
                table: "Bookings");

            migrationBuilder.DropColumn(
                name: "CleaningFee",
                table: "Bookings");
        }
    }
}
