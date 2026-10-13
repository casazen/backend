using System.Text;
using Casazen.Core.TouristTax;

namespace Casazen.Core.Regulatory;

/// <summary>Names of the comuni: the comparable form used to match and search them, and the slug used in public URLs.</summary>
public static class ComuneNames
{
    /// <summary>
    /// Comparable form of a name: accents removed, lower case, every run of characters that are not letters or digits
    /// (spaces, apostrophes, hyphens, slashes) one space. The same normalization as the tourist tax rates
    /// (<see cref="TouristTaxComune.NormalizeName"/>), so "Forlì", "forli" and "FORLI" are one name.
    /// </summary>
    public static string Normalize(string? name) => TouristTaxComune.NormalizeName(name);

    /// <summary>
    /// URL slug of a name: its normalized form with hyphens (<c>Reggio nell'Emilia</c> → <c>reggio-nell-emilia</c>).
    /// Empty when the name has no letters or digits.
    /// </summary>
    public static string Slugify(string? name)
    {
        var normalized = Normalize(name);
        if (normalized.Length == 0)
            return string.Empty;

        var builder = new StringBuilder(normalized.Length);
        foreach (var c in normalized)
            builder.Append(c == ' ' ? '-' : c);
        return builder.ToString();
    }
}
