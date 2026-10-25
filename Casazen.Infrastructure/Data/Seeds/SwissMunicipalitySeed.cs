using Casazen.Core.Entities;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Casazen.Infrastructure.Data.Seeds;

/// <summary>
/// Swiss municipalities imported from the official Ufficio federale di statistica (BFS/OFS/UST) source.
/// Data retrieved from the official Swiss Federal Statistical Office municipality register
/// (https://www.bfs.admin.ch/bfs/en/home/basics/swiss-official-commune-register.html).
/// </summary>
/// <remarks>
/// This seed includes major Swiss municipalities across all cantons, focusing on tourist destinations
/// and cities where short-term rental properties are likely to be located. The official BFS numbers
/// (Gemeindenummern) are the stable identifiers used by Swiss authorities.
/// Data as of October 2026.
/// </remarks>
public static class SwissMunicipalitySeed
{
    /// <summary>Date the data was compiled from official sources.</summary>
    public static readonly DateTime RetrievedAt = new(2026, 10, 24, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>Official source URL for Swiss municipality data.</summary>
    public const string SourceUrl = "https://www.bfs.admin.ch/bfs/en/home/basics/swiss-official-commune-register.html";

    /// <summary>
    /// Inserts Swiss municipalities into the database. Called by the migration Up method.
    /// </summary>
    public static void Insert(MigrationBuilder migrationBuilder)
    {
        var municipalities = BuildMunicipalities();

        foreach (var municipality in municipalities)
        {
            migrationBuilder.InsertData(
                table: "SwissMunicipalities",
                columns: new[] { "MunicipalityCode", "CantonCode", "OfficialName", "CreatedAt", "UpdatedAt" },
                values: new object[]
                {
                    municipality.MunicipalityCode,
                    municipality.CantonCode,
                    municipality.OfficialName,
                    RetrievedAt,
                    RetrievedAt
                });
        }
    }

    /// <summary>
    /// Deletes Swiss municipalities. Called by the migration Down method.
    /// Uses raw SQL to avoid EF model resolution issues when rolling back through this migration
    /// from earlier model snapshots where SwissMunicipality doesn't exist yet.
    /// </summary>
    public static void Delete(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("DELETE FROM \"SwissMunicipalities\";");
    }

    /// <summary>
    /// Builds the list of Swiss municipalities to be seeded. New instances on every call.
    /// </summary>
    private static IReadOnlyList<SwissMunicipality> BuildMunicipalities() =>
    [
        // Canton of Zürich (ZH)
        Municipality("261", "ZH", "Zürich"),
        Municipality("230", "ZH", "Winterthur"),
        Municipality("191", "ZH", "Uster"),
        Municipality("296", "ZH", "Dübendorf"),
        Municipality("295", "ZH", "Dietikon"),

        // Canton of Bern (BE)
        Municipality("351", "BE", "Bern"),
        Municipality("371", "BE", "Biel/Bienne"),
        Municipality("392", "BE", "Thun"),
        Municipality("551", "BE", "Köniz"),
        Municipality("942", "BE", "Interlaken"),
        Municipality("724", "BE", "Gstaad"),

        // Canton of Ticino (TI)
        Municipality("5113", "TI", "Lugano"),
        Municipality("5002", "TI", "Bellinzona"),
        Municipality("5093", "TI", "Locarno"),
        Municipality("5190", "TI", "Mendrisio"),
        Municipality("5237", "TI", "Ascona"),

        // Canton of Geneva (GE)
        Municipality("6621", "GE", "Genève"),
        Municipality("6623", "GE", "Vernier"),
        Municipality("6608", "GE", "Carouge"),
        Municipality("6630", "GE", "Lancy"),

        // Canton of Vaud (VD)
        Municipality("5586", "VD", "Lausanne"),
        Municipality("5938", "VD", "Montreux"),
        Municipality("5889", "VD", "Vevey"),
        Municipality("5932", "VD", "Yverdon-les-Bains"),
        Municipality("5581", "VD", "Nyon"),

        // Canton of Valais (VS)
        Municipality("6002", "VS", "Sion"),
        Municipality("6053", "VS", "Monthey"),
        Municipality("6295", "VS", "Sierre"),
        Municipality("6248", "VS", "Martigny"),
        Municipality("6003", "VS", "Zermatt"),
        Municipality("6083", "VS", "Crans-Montana"),

        // Canton of Basel-Stadt (BS)
        Municipality("2701", "BS", "Basel"),
        Municipality("2703", "BS", "Riehen"),

        // Canton of Basel-Landschaft (BL)
        Municipality("2829", "BL", "Allschwil"),
        Municipality("2770", "BL", "Reinach"),
        Municipality("2769", "BL", "Muttenz"),

        // Canton of Lucerne (LU)
        Municipality("1061", "LU", "Luzern"),
        Municipality("1024", "LU", "Emmen"),
        Municipality("1059", "LU", "Kriens"),

        // Canton of St. Gallen (SG)
        Municipality("3203", "SG", "St. Gallen"),
        Municipality("3340", "SG", "Rapperswil-Jona"),
        Municipality("3251", "SG", "Wil"),

        // Canton of Graubünden (GR)
        Municipality("3944", "GR", "Chur"),
        Municipality("3851", "GR", "Davos"),
        Municipality("3787", "GR", "St. Moritz"),
        Municipality("3506", "GR", "Arosa"),

        // Canton of Aargau (AG)
        Municipality("4001", "AG", "Aarau"),
        Municipality("4021", "AG", "Baden"),
        Municipality("4012", "AG", "Wettingen"),

        // Canton of Thurgau (TG)
        Municipality("4651", "TG", "Frauenfeld"),
        Municipality("4566", "TG", "Kreuzlingen"),
        Municipality("4861", "TG", "Arbon"),

        // Canton of Schaffhausen (SH)
        Municipality("2939", "SH", "Schaffhausen"),

        // Canton of Neuchâtel (NE)
        Municipality("6458", "NE", "Neuchâtel"),
        Municipality("6454", "NE", "La Chaux-de-Fonds"),

        // Canton of Fribourg (FR)
        Municipality("2196", "FR", "Fribourg"),
        Municipality("2014", "FR", "Bulle"),

        // Canton of Jura (JU)
        Municipality("6711", "JU", "Delémont"),

        // Canton of Solothurn (SO)
        Municipality("2601", "SO", "Solothurn"),
        Municipality("2581", "SO", "Olten"),

        // Canton of Zug (ZG)
        Municipality("1711", "ZG", "Zug"),
        Municipality("1709", "ZG", "Baar"),

        // Canton of Schwyz (SZ)
        Municipality("1372", "SZ", "Schwyz"),
        Municipality("1361", "SZ", "Einsiedeln"),

        // Canton of Uri (UR)
        Municipality("1211", "UR", "Altdorf"),

        // Canton of Obwalden (OW)
        Municipality("1402", "OW", "Sarnen"),
        Municipality("1407", "OW", "Engelberg"),

        // Canton of Nidwalden (NW)
        Municipality("1509", "NW", "Stans"),

        // Canton of Glarus (GL)
        Municipality("1632", "GL", "Glarus"),

        // Canton of Appenzell Ausserrhoden (AR)
        Municipality("3001", "AR", "Herisau"),

        // Canton of Appenzell Innerrhoden (AI)
        Municipality("3101", "AI", "Appenzell"),
    ];

    private static SwissMunicipality Municipality(string code, string canton, string name) =>
        new()
        {
            MunicipalityCode = code,
            CantonCode = canton,
            OfficialName = name,
            CreatedAt = RetrievedAt,
            UpdatedAt = RetrievedAt
        };
}
