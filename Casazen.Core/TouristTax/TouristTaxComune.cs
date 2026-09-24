using System.Globalization;
using System.Text;
using Casazen.Core.Entities;

namespace Casazen.Core.TouristTax;

/// <summary>
/// Comune whose tourist tax rates are looked up (A8-23). When both the comune and a rate carry an ISTAT code, they
/// match by code only; otherwise by the normalized name (<see cref="NormalizeName"/>), so "roma", " ROMA " and "Roma"
/// find the same rates, and "Forlì" / "Forli" or "Reggio nell'Emilia" / "Reggio nell’Emilia" too.
/// </summary>
/// <param name="IstatCode">ISTAT code (6 digits) when trusted, e.g. from <c>ItalianComuneRegistry</c>; null otherwise.</param>
/// <param name="Name">Name of the comune (for a property: the city typed by the host).</param>
public sealed record TouristTaxComune(string? IstatCode, string? Name)
{
    /// <summary>
    /// Comune of a property. Properties have no ISTAT code yet (only the free-text city), so the match is by name.
    /// </summary>
    public static TouristTaxComune ForProperty(Property property)
    {
        ArgumentNullException.ThrowIfNull(property);
        return new TouristTaxComune(null, property.City);
    }

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
