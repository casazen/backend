using System.Text;
using Casazen.Core.TouristTax;

namespace Casazen.Core.Regulatory;

public static class ComuneNames
{
    public static string Normalize(string? name) => TouristTaxComune.NormalizeName(name);

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
