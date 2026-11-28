using System.Text.RegularExpressions;

namespace Casazen.Core.Regulatory;

/// <summary>
/// Shape of the identifiers of a comune in the official ISTAT list (SU-04): the single place that says what an ISTAT
/// code, a cadastral code and a province code look like. A shape check is never a substitute for finding the code in
/// the imported list (<c>IComuneDirectory</c>).
/// </summary>
public static partial class ComuneRules
{
    public const int IstatCodeLength = 6;
    public const int CadastralCodeLength = 4;
    public const int ProvinceCodeLength = 2;
    public const int RegionIstatCodeLength = 2;
    public const int NameMaxLength = 100;
    public const int DisplayNameMaxLength = 150;
    public const int RegionNameMaxLength = 100;

    /// <summary>ISTAT code of a comune: 3 digits of the province and 3 of the comune, as text (leading zeros matter).</summary>
    public const string IstatCodePattern = "^[0-9]{6}$";

    public const string CadastralCodePattern = "^[A-Z][0-9]{3}$";

    public const string ProvinceCodePattern = "^[A-Z]{2}$";

    [GeneratedRegex(IstatCodePattern, RegexOptions.CultureInvariant)]
    private static partial Regex IstatRegex();

    [GeneratedRegex(CadastralCodePattern, RegexOptions.CultureInvariant)]
    private static partial Regex CadastralRegex();

    [GeneratedRegex(ProvinceCodePattern, RegexOptions.CultureInvariant)]
    private static partial Regex ProvinceRegex();

    public static bool IsIstatCode(string? value) => value is not null && IstatRegex().IsMatch(value);

    public static bool IsCadastralCode(string? value) => value is not null && CadastralRegex().IsMatch(value);

    public static bool IsProvinceCode(string? value) => value is not null && ProvinceRegex().IsMatch(value);

    /// <summary>The ISTAT code trimmed, or <c>null</c> when it is blank or not six digits.</summary>
    public static string? NormalizeIstatCode(string? value)
    {
        var trimmed = value?.Trim();
        return IsIstatCode(trimmed) ? trimmed : null;
    }

    public static string? NormalizeCadastralCode(string? value)
    {
        var upper = value?.Trim().ToUpperInvariant();
        return IsCadastralCode(upper) ? upper : null;
    }
}
