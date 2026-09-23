using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <summary>
    /// CO-01 (A5-05, R-02): stores every existing CIN in the normalized form of
    /// <c>Casazen.Core.Regulatory.CinFormat.Normalize</c>: whitespace (non-breaking spaces included) and
    /// hyphens/dashes removed, upper case; a value with nothing left becomes NULL (no CIN).
    /// </summary>
    /// <remarks>
    /// Data only, no schema change. CINs that still do not match the official format (for example the old
    /// invented <c>IT-12345-0123456789</c>) are NOT deleted: the CIN status is computed on read, so they show up
    /// as "invalid" to the host and in the admin report. Down is a no-op: the original spacing and case are
    /// not kept and are not needed. Runbook: <c>docs/runbooks/cin-format.md</c>.
    /// </remarks>
    public partial class NormalizeCinCodes : Migration
    {
        /// <summary>
        /// PostgreSQL normalization, kept in sync with <c>CinFormat.Normalize</c> (same whitespace and dash
        /// set). Public so that a PostgreSQL test can check it against the C# implementation.
        /// </summary>
        public const string NormalizeSql = """
            UPDATE "Properties" AS p
            SET "CinCode" = n."Normalized"
            FROM (
                SELECT "Id",
                       NULLIF(upper(regexp_replace(
                           "CinCode",
                           '[[:space:]\u0085\u00A0\u1680\u2000-\u200A\u2028\u2029\u202F\u205F\u3000\u2010-\u2015\u2212-]',
                           '',
                           'g')), '') AS "Normalized"
                FROM "Properties"
                WHERE "CinCode" IS NOT NULL
            ) AS n
            WHERE p."Id" = n."Id"
              AND p."CinCode" IS DISTINCT FROM n."Normalized";
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(NormalizeSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // No-op: the original spacing and case of the CINs cannot be restored.
        }
    }
}
