using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <summary>
    /// The check of the deadline of the ISTAT region table (E21/F01/US01/T01) refused a day on a deadline that is not monthly, but let a monthly deadline with
    /// no day in: in SQL a check passes when its condition is NULL, and <c>NULL BETWEEN 1 AND 31</c> is NULL. The monthly branch now says <c>IS NOT NULL</c>.
    /// Every stored row already satisfies it (the seed has a day on each of its four monthly rows and the staff write goes through the service, which asks
    /// for the day); a row that did not, written by hand, becomes «not stated» first, so the new check can never stop the migration (a deadline is never guessed).
    /// </summary>
    /// <inheritdoc />
    public partial class FixIstatDeadlineCheck : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "UPDATE \"IstatRegionRules\" SET \"DeadlineKind\" = 0 WHERE \"DeadlineKind\" = 2 AND \"DeadlineDay\" IS NULL;");

            migrationBuilder.DropCheckConstraint(
                name: "CK_IstatRegionRules_Deadline",
                table: "IstatRegionRules");

            migrationBuilder.AddCheckConstraint(
                name: "CK_IstatRegionRules_Deadline",
                table: "IstatRegionRules",
                sql: "(\"DeadlineKind\" = 2 AND \"DeadlineDay\" IS NOT NULL AND \"DeadlineDay\" BETWEEN 1 AND 31) OR (\"DeadlineKind\" <> 2 AND \"DeadlineDay\" IS NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_IstatRegionRules_Deadline",
                table: "IstatRegionRules");

            migrationBuilder.AddCheckConstraint(
                name: "CK_IstatRegionRules_Deadline",
                table: "IstatRegionRules",
                sql: "(\"DeadlineKind\" = 2 AND \"DeadlineDay\" BETWEEN 1 AND 31) OR (\"DeadlineKind\" <> 2 AND \"DeadlineDay\" IS NULL)");
        }
    }
}
