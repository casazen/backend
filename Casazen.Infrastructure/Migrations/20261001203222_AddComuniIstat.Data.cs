using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <summary>
    /// Data of the SU-04 migration, with inline values (never reads the application's code tables, so the migration does the
    /// same thing whatever they become later; same convention as <c>AddLtrReferenceDataAdmin.Data.cs</c>).
    /// </summary>
    /// <remarks>
    /// The hardcoded registry <c>ItalianComuneRegistry</c> (removed by SU-04) had four ISTAT codes that are not the ones of the
    /// official list (A4-12, A5-34, A8-24): Torino carried the code of Genova, Bellagio and Menaggio had old codes, Varenna
    /// carried a code of a different comune. SEO pages and signup attributions of the pilot comuni were stored with them, so
    /// they are moved to the official codes (ISTAT, list at 21/02/2026; the codes are the ones of
    /// <c>Casazen.Infrastructure/Data/Seeds/comuni-istat.csv</c>, checked by a test against the list):
    /// <list type="bullet">
    /// <item>Torino 010025 to 001272 (010025 is Genova);</item>
    /// <item>Bellagio 013040 to 013250;</item>
    /// <item>Menaggio 013133 to 013145;</item>
    /// <item>Varenna 013182 to 097084.</item>
    /// </list>
    /// A SEO page is moved only when its slug names the comune (<c>.../torino</c>), so a page of another comune that happens to
    /// carry an old code is never touched, and never onto a code that already has a page of the same type (unique index).
    /// Signup attributions have no slug of the comune: the registry was the only way a code got in, so an old code there is the
    /// comune the registry said. Nothing else stored these codes (Property and supplier ISTAT columns are new in this migration,
    /// invites keep their cadastral code, tourist tax rates are keyed by name or by the right code).
    /// </remarks>
    public partial class AddComuniIstat
    {
        /// <summary>Old code of the removed registry, official ISTAT code, slug of the comune in the SEO page paths.</summary>
        private static readonly (string OldCode, string NewCode, string Slug)[] RegistryCodeCorrections =
        [
            ("010025", "001272", "torino"),
            ("013040", "013250", "bellagio"),
            ("013133", "013145", "menaggio"),
            ("013182", "097084", "varenna"),
        ];

        private static void ApplyData(MigrationBuilder migrationBuilder)
        {
            foreach (var (oldCode, newCode, slug) in RegistryCodeCorrections)
                MoveCode(migrationBuilder, oldCode, newCode, slug);
        }

        private static void RevertData(MigrationBuilder migrationBuilder)
        {
            foreach (var (oldCode, newCode, slug) in RegistryCodeCorrections)
                MoveCode(migrationBuilder, newCode, oldCode, slug);
        }

        // The codes and the slug are constants of this file, never user input.
        private static void MoveCode(MigrationBuilder migrationBuilder, string from, string to, string slug)
        {
            migrationBuilder.Sql($"""
                UPDATE "SeoContentPages" AS p
                SET "ComuneCode" = '{to}'
                WHERE p."ComuneCode" = '{from}'
                  AND p."Slug" LIKE '%/{slug}'
                  AND NOT EXISTS (
                      SELECT 1 FROM "SeoContentPages" AS other
                      WHERE other."ComuneCode" = '{to}' AND other."PageType" = p."PageType");
                """);

            migrationBuilder.Sql($"""
                UPDATE "SignupAttributions"
                SET "ComuneCode" = '{to}'
                WHERE "ComuneCode" = '{from}';
                """);
        }
    }
}
