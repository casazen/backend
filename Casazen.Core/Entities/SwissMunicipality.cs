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
    [Key, MaxLength(10)]
    public string MunicipalityCode { get; set; } = string.Empty;

    [Required, MaxLength(2)]
    public string CantonCode { get; set; } = string.Empty;

    [Required, MaxLength(100)]
    public string OfficialName { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
