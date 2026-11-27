using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Casazen.Core.Entities;

/// <summary>
/// A municipality of Switzerland ("Comune" in Ticino, "Commune" in French-speaking areas, "Gemeinde" in German-speaking areas). Platform reference
/// data for Swiss properties (Country = 'CH'): every municipality of the official directory of the localities of swisstopo («Amtliches
/// Ortschaftenverzeichnis», E29/F01/US01/T01, NF-124), loaded only from the official seed (<c>Data/Seeds/swiss/swiss-localities.csv</c>), never
/// written by a user. MunicipalityCode is the official BFS number (Gemeindenummer); CantonCode is the 2-character ISO 3166-2 code (e.g. TI, ZH, BE).
/// Its localities and postal codes are <see cref="SwissLocality"/>.
/// </summary>
[Table("SwissMunicipalities")]
public class SwissMunicipality
{
    /// <summary>
    /// Official BFS municipality number (Gemeindenummer / Numéro OFS / Numero UST), digits without leading zeros. Primary key. Examples: "5192"
    /// (Lugano), "5113" (Locarno), "261" (Zürich), "351" (Bern).
    /// </summary>
    [Key, MaxLength(10)]
    public string MunicipalityCode { get; set; } = string.Empty;

    /// <summary>
    /// ISO 3166-2:CH canton code, 2 characters (e.g. "TI" for Ticino, "ZH" for Zürich, "BE" for Bern).
    /// </summary>
    [Required, MaxLength(2)]
    public string CantonCode { get; set; } = string.Empty;

    /// <summary>
    /// Official municipality name as the directory writes it (Gemeindename). For bilingual or trilingual cantons, this is the official variant of
    /// the directory. Examples: "Lugano", "Zürich", "Biel/Bienne".
    /// </summary>
    [Required, MaxLength(100)]
    public string OfficialName { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
