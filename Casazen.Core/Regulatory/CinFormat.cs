using System.Text;
using System.Text.RegularExpressions;
using Casazen.Core.Enums;

namespace Casazen.Core.Regulatory;

/// <summary>
/// Single source of truth for the CIN (Codice Identificativo Nazionale, D.L. 145/2023 art. 13-ter) format.
/// Every backend check goes through this class; the frontend mirrors it in <c>src/lib/cin-format.ts</c>
/// (keep the two in sync).
/// </summary>
/// <remarks>
/// <para>
/// Official composition (MiTur interoperability decree prot. 16726 of 06/06/2024): <c>IT</c> + ISTAT
/// province code (3 digits) + ISTAT comune code (3 digits) + ISTAT category (2 characters) + random
/// alphanumeric string (at most 8). Real CINs are 18 characters with a letter+digit category, e.g.
/// <c>IT058091C27G5FFZDZ</c>. Sources: <c>.claude/context/regulations/cin.md</c>, section
/// "Formato verificato (2026-09)".
/// </para>
/// <para>
/// Input is normalized first (<see cref="Normalize"/>: no whitespace, no hyphens/dashes, upper case) and
/// then matched against <see cref="Pattern"/>. The category is only "2 characters" in the official text, so
/// it accepts any letter or digit. That also lets the old format CasaZen invented
/// (<c>IT-XXXXX-XXXXXXXXXX</c>, i.e. <c>IT</c> + 15 digits once normalized) through, so
/// <see cref="LegacyPattern"/> rejects it explicitly.
/// </para>
/// </remarks>
public static partial class CinFormat
{
    /// <summary>Valid normalized CIN (ASCII digits and letters only).</summary>
    public const string Pattern = "^IT[0-9]{6}[A-Z0-9]{2}[A-Z0-9]{1,8}$";

    /// <summary>
    /// The invented format previously required by CasaZen (<c>IT-12345-0123456789</c>), normalized. Never a
    /// real CIN (no category letter, 5-digit "ISTAT" part): always invalid.
    /// </summary>
    public const string LegacyPattern = "^IT[0-9]{15}$";

    /// <summary>
    /// Stable error code (API <c>code</c>) for a CIN that does not match the official format.
    /// </summary>
    public const string InvalidFormatCode = "invalid_cin_format";

    /// <summary>SharedResources key of the "invalid CIN format" message (also used by <c>[CinCode]</c>).</summary>
    public const string InvalidFormatMessageKey = "CinInvalidFormat";

    [GeneratedRegex(Pattern, RegexOptions.CultureInvariant)]
    private static partial Regex CinRegex();

    [GeneratedRegex(LegacyPattern, RegexOptions.CultureInvariant)]
    private static partial Regex LegacyRegex();

    /// <summary>
    /// Normalized form to validate, store and display: every whitespace character (including non-breaking
    /// spaces) and every hyphen/dash (<c>- ‐ ‑ ‒ – — ― −</c>) removed, upper case (invariant culture).
    /// Other characters (dots, slashes, a "CIN:" prefix) are kept so that validation rejects them instead of
    /// silently "cleaning" the input. Returns <c>null</c> when nothing is left (no CIN).
    /// </summary>
    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (char.IsWhiteSpace(c) || IsDash(c))
                continue;

            builder.Append(char.ToUpperInvariant(c));
        }

        return builder.Length == 0 ? null : builder.ToString();
    }

    /// <summary>True when <paramref name="value"/>, once normalized, is a CIN in the official format.</summary>
    public static bool IsValid(string? value)
    {
        var normalized = Normalize(value);
        return normalized is not null && CinRegex().IsMatch(normalized) && !LegacyRegex().IsMatch(normalized);
    }

    /// <summary>Computed CIN status: <see cref="CinStatus.Missing"/> for no CIN, else valid or invalid.</summary>
    public static CinStatus GetStatus(string? value)
    {
        if (Normalize(value) is null)
            return CinStatus.Missing;

        return IsValid(value) ? CinStatus.Valid : CinStatus.Invalid;
    }

    /// <summary>
    /// 6-digit ISTAT comune code embedded in a valid CIN (positions 3–8), or <c>null</c> when the CIN is
    /// missing or invalid.
    /// </summary>
    public static string? GetIstatComuneCode(string? value) =>
        IsValid(value) ? Normalize(value)![2..8] : null;

    /// <summary>
    /// Extension point for the non-blocking ISTAT warning: <c>true</c> only when the CIN is valid, a trusted
    /// 6-digit ISTAT comune code of the property is known and the two differ. Never a validation error:
    /// the CIN stays the same after relocations, reclassifications and comune mergers. Returns <c>false</c>
    /// when there is nothing reliable to compare.
    /// </summary>
    /// <remarks>
    /// Not wired to any endpoint yet: properties only carry a free-text <c>City</c> and
    /// <c>ItalianComuneRegistry</c> is not a reliable city→ISTAT mapping. Call it once the property has a
    /// trusted ISTAT code (ISTAT registry, task SU-04).
    /// </remarks>
    public static bool HasIstatComuneMismatch(string? cin, string? trustedIstatComuneCode)
    {
        var embedded = GetIstatComuneCode(cin);
        var expected = trustedIstatComuneCode?.Trim();
        if (embedded is null || expected is null || expected.Length != 6 || !expected.All(char.IsAsciiDigit))
            return false;

        return !string.Equals(embedded, expected, StringComparison.Ordinal);
    }

    private static bool IsDash(char c) =>
        c is '-' or '\u2010' or '\u2011' or '\u2012' or '\u2013' or '\u2014' or '\u2015' or '\u2212';
}
