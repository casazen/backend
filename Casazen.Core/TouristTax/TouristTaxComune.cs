using System.Globalization;
using System.Text;
using Casazen.Core.Entities;

namespace Casazen.Core.TouristTax;

/// <summary>
/// Comune whose tourist tax rates are looked up (A8-23). When both the comune and a rate carry an ISTAT code, they
/// match by code only; otherwise by the normalized name (<see cref="NormalizeName"/>), so "roma", " ROMA " and "Roma"
/// find the same rates, and "Forlì" / "Forli" or "Reggio nell'Emilia" / "Reggio nell’Emilia" too.
/// </summary>
/// <param name="IstatCode">ISTAT code (6 digits) when trusted, i.e. a comune of the official ISTAT list (<c>Comuni</c>, SU-04); null otherwise.</param>
/// <param name="Name">Name of the comune (for a property: the city typed by the host).</param>
public sealed record TouristTaxComune(string? IstatCode, string? Name)
{
    /// <summary>
    /// Comune of a property: the ISTAT code the host chose from the official list (SU-04) when there is one, so a rate that
    /// carries a code is matched by code; the written city otherwise (and for the rates without a code), matched by name.
    /// </summary>
    public static TouristTaxComune ForProperty(Property property)
    {
        ArgumentNullException.ThrowIfNull(property);

        // The rates are those of the Italian comuni. A house in another country (CHF-MONEY: Switzerland, whose Kurtaxe is not modelled) is
        // never matched to one by a name that happens to be the same: its amount would be euros added to a total in another currency.
        return IsItalianCountry(property.Country)
            ? new TouristTaxComune(property.ComuneIstatCode, property.City)
            : new TouristTaxComune(null, null);
    }

    /// <summary>
    /// True when a property with this country code is in Italy: no country at all (an old row) or <c>IT</c>, in any case. The one rule of the
    /// properties whose tourist tax is looked up (<see cref="ForProperty"/>), added up per comune and paid (<c>ITouristTaxSummaryService</c>): a
    /// house in another country is never taken for an Italian comune of the same name.
    /// </summary>
    public static bool IsItalianCountry(string? country) =>
        string.IsNullOrWhiteSpace(country) || string.Equals(country, "IT", StringComparison.OrdinalIgnoreCase);

    /// <summary>True when there is nothing to look up (no code and an empty name).</summary>
    public bool IsEmpty => string.IsNullOrWhiteSpace(IstatCode) && NormalizeName(Name).Length == 0;

    /// <summary>True when <paramref name="rate"/> belongs to this comune (active or not, whatever its dates).</summary>
    public bool Matches(TouristTaxRate rate)
    {
        ArgumentNullException.ThrowIfNull(rate);

        var code = IstatCode?.Trim();
        var rateCode = rate.IstatCode?.Trim();
        if (!string.IsNullOrEmpty(code) && !string.IsNullOrEmpty(rateCode))
            return string.Equals(code, rateCode, StringComparison.Ordinal);

        var name = NormalizeName(Name);
        return name.Length > 0 && name == NormalizeName(rate.City);
    }

    /// <summary>
    /// Comparable form of a comune name: accents removed, lower case (invariant), every run of characters that are
    /// not letters or digits (spaces, apostrophes, hyphens, dots) turned into one space, trimmed.
    /// </summary>
    public static string NormalizeName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return string.Empty;

        var builder = new StringBuilder(name.Length);
        var pendingSeparator = false;
        foreach (var ch in name.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark)
                continue;

            if (!char.IsLetterOrDigit(ch))
            {
                pendingSeparator = true;
                continue;
            }

            if (pendingSeparator && builder.Length > 0)
                builder.Append(' ');
            pendingSeparator = false;
            builder.Append(char.ToLowerInvariant(ch));
        }

        return builder.ToString().Normalize(NormalizationForm.FormC);
    }
}
