using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Globalization;
using System.Text;

namespace Casazen.Core.Entities;

/// <summary>
/// A locality of the official directory of swisstopo («Amtliches Ortschaftenverzeichnis», E29/F01/US01/T01, NF-124): a postal code with its add-on
/// and the name of the place, and the municipality it lies in. A locality that lies in several municipalities has one row for each (the postal
/// locality «6600 Locarno» lies in Locarno and in Muralto). Platform reference data, the same for every org: loaded only from the official seed
/// (<c>Data/Seeds/swiss/swiss-localities.csv</c>), never written by a user. The key is the postal code, the add-on, the municipality and the name.
/// </summary>
[Table("SwissLocalities")]
public class SwissLocality
{
    /// <summary>Longest locality name stored.</summary>
    public const int NameMaxLength = 100;

    /// <summary>The four digits of the postal code (PLZ4), e.g. <c>6900</c>.</summary>
    [Required, MaxLength(4)]
    public string PostalCode { get; set; } = string.Empty;

    /// <summary>The two-digit add-on of the postal code (Zusatzziffer): <c>6900</c> <c>00</c> is Lugano, <c>6900</c> <c>05</c> Massagno.</summary>
    [Required, MaxLength(2)]
    public string AddOn { get; set; } = string.Empty;

    /// <summary>The name of the locality as the directory writes it (Ortschaftsname), e.g. <c>Lugano</c>, <c>Brè sopra Lugano</c>.</summary>
    [Required, MaxLength(NameMaxLength)]
    public string Name { get; set; } = string.Empty;

    /// <summary><see cref="Name"/> as the search compares it (<see cref="NormalizeName"/>): lower case, without diacritics.</summary>
    [Required, MaxLength(NameMaxLength)]
    public string NormalizedName { get; set; } = string.Empty;

    /// <summary>The BFS number of the municipality (<see cref="SwissMunicipality.MunicipalityCode"/>) the locality, or this part of it, lies in.</summary>
    [Required, MaxLength(10)]
    public string MunicipalityCode { get; set; } = string.Empty;

    /// <summary>The two letters of the canton of the municipality (ISO 3166-2:CH), e.g. <c>TI</c>.</summary>
    [Required, MaxLength(2)]
    public string CantonCode { get; set; } = string.Empty;

    /// <summary>
    /// A place name as the search and the resolver of the place of a property compare it: lower case (invariant), without diacritics
    /// (<c>Zürich</c> and <c>zurich</c>, <c>Brè</c> and <c>bre</c> are the same), typographic apostrophes as <c>'</c>, spaces trimmed and
    /// collapsed. Nothing else is changed: «San» and «S.», «St.» and «Sankt» stay different. <c>null</c> or blank gives an empty text.
    /// </summary>
    public static string NormalizeName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var decomposed = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        var pendingSpace = false;
        foreach (var ch in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark)
                continue;

            if (char.IsWhiteSpace(ch))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(ch is '’' or '‘' or '`' or '´' ? '\'' : char.ToLowerInvariant(ch));
        }

        return builder.ToString().Normalize(NormalizationForm.FormC);
    }
}
