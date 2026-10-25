using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Casazen.Core.Entities;

/// <summary>
/// A municipality of Switzerland ("Comune" in Ticino, "Commune" in French-speaking areas, "Gemeinde" in German-speaking areas),
/// imported from the official Ufficio federale di statistica (BFS/OFS/UST) source. Platform reference data for Swiss properties
/// (Country = 'CH'). MunicipalityCode is the official BFS number (Gemeindenummer); CantonCode is the 2-character ISO 3166-2 code
/// (e.g. TI, ZH, BE).
/// </summary>
[Table("SwissMunicipalities")]
public class SwissMunicipality
{
    /// <summary>
    /// Official BFS municipality number (Gemeindenummer / Numéro OFS / Numero UST). Primary key. Examples: "5113" (Lugano),
    /// "261" (Zürich), "351" (Bern).
    /// </summary>
    [Key, MaxLength(10)]
    public string MunicipalityCode { get; set; } = string.Empty;

    /// <summary>
    /// ISO 3166-2:CH canton code, 2 characters (e.g. "TI" for Ticino, "ZH" for Zürich, "BE" for Bern).
    /// </summary>
    [Required, MaxLength(2)]
    public string CantonCode { get; set; } = string.Empty;

    /// <summary>
    /// Official municipality name as provided by the Ufficio federale di statistica. For bilingual or trilingual cantons,
    /// this is typically the primary official language variant. Examples: "Lugano", "Zürich", "Bern".
    /// </summary>
    [Required, MaxLength(100)]
    public string OfficialName { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
