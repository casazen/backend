using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <summary>
    /// DB-01: the public site gets three new themes (terra, mare, citta) and a font pair chosen apart from the theme.
    /// Adds <c>Orgs.PublicFontId</c> (null = the font of the theme) and rewrites the stored theme ids. The id <c>mare</c> is
    /// reused by a new look (petrol blue), so an org that had the old <c>mare</c> (terracotta on cream) has to move to
    /// <c>terra</c>, which is that look, in the same step that makes the new tokens live: <c>mare</c> and <c>montagna</c>
    /// become <c>terra</c>, <c>urban</c> becomes <c>citta</c>. The two themes that disappear had their own colour: an org that
    /// never picked a colour of its own keeps the one its theme gave (the colour is the part of the look that survives).
    /// </summary>
    public partial class AddPublicSiteFontAndThemeIds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PublicFontId",
                table: "Orgs",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            // The colour first (the statements read the old ids), then the ids. Stored ids were validated lower case, but a value
            // written before the themes were validated may carry spaces or capitals: match what the reading code accepts.
            migrationBuilder.Sql("""
                UPDATE "Orgs" SET "ThemeColor" = '#2e6a4a'
                WHERE lower(btrim("PublicThemeId")) = 'montagna' AND ("ThemeColor" IS NULL OR btrim("ThemeColor") = '');
                """);
            migrationBuilder.Sql("""
                UPDATE "Orgs" SET "ThemeColor" = '#2f3bd1'
                WHERE lower(btrim("PublicThemeId")) = 'urban' AND ("ThemeColor" IS NULL OR btrim("ThemeColor") = '');
                """);
            migrationBuilder.Sql("""
                UPDATE "Orgs" SET "PublicThemeId" = 'terra'
                WHERE lower(btrim("PublicThemeId")) IN ('mare', 'montagna');
                """);
            migrationBuilder.Sql("""
                UPDATE "Orgs" SET "PublicThemeId" = 'citta'
                WHERE lower(btrim("PublicThemeId")) = 'urban';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Best effort: the old code knows only mare, montagna and urban. terra is what mare was and citta is the closest of
            // urban; the new mare (petrol blue) has no old twin and an old release shows it as terracotta. The colours written by
            // Up stay (they are the host's colour now) and montagna cannot be told apart from mare any more.
            migrationBuilder.Sql("""UPDATE "Orgs" SET "PublicThemeId" = 'mare' WHERE "PublicThemeId" = 'terra';""");
            migrationBuilder.Sql("""UPDATE "Orgs" SET "PublicThemeId" = 'urban' WHERE "PublicThemeId" = 'citta';""");

            migrationBuilder.DropColumn(
                name: "PublicFontId",
                table: "Orgs");
        }
    }
}
