using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <summary>
    /// Re-ranks Alloggiati duplicates that are still stored so a row with a receipt wins over a newer blank one.
    /// </summary>
    /// <remarks>
    /// <para><c>20260924012639_AlloggiatiHonestStatus</c> is already applied on existing databases and its
    /// <c>Up</c> does not run again. That migration kept the newest row. This one applies the receipt-first rule to
    /// any duplicate <c>(BookingId, GuestId)</c> still present. A receipt deleted by the original merge cannot be
    /// restored. Down does not put removed rows back.</para>
    /// </remarks>
    public partial class PreferAlloggiatiReceiptDuplicates : Migration
    {
        /// <summary>Keeps one row per (BookingId, GuestId): receipt-bearing rows win, then the most recently updated one.</summary>
        public const string Sql = """
            WITH ranked AS (
                SELECT "Id",
                       row_number() OVER (
                           PARTITION BY "BookingId", "GuestId"
                           ORDER BY (btrim(coalesce("ConfirmationNumber", '')) <> '') DESC,
                                    "UpdatedAt" DESC,
                                    "Id" DESC) AS rn
                FROM "AlloggiatiWebReports"
            )
            DELETE FROM "AlloggiatiWebReports" AS r
            USING ranked
            WHERE ranked."Id" = r."Id"
              AND ranked.rn > 1;
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(Sql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Rows removed as duplicates are gone.
        }
    }
}
