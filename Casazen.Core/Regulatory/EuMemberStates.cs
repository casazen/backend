namespace Casazen.Core.Regulatory;

/// <summary>
/// The 27 member states of the European Union as ISO 3166-1 alpha-2 codes (LT-07, A7-08). Single list used to decide
/// whether a lease party is "extra-UE": a tenant whose citizenship is not one of these codes is a "straniero o apolide"
/// for the written communication to the local public-security authority of art. 7 D.Lgs. 286/1998
/// (<c>.claude/context/regulations/fiscale.md</c> L13-L14), so the Questura item of the lease applies.
/// </summary>
/// <remarks>
/// <para>Membership checked on 2026-09-25: 27 states since the United Kingdom left on 31/01/2020. Source: European Union,
/// "EU countries", https://european-union.europa.eu/principles-countries-history/eu-countries_en.</para>
/// <para>Codes are ISO 3166-1 (Greece is <c>GR</c>, not the EU abbreviation <c>EL</c>). Any other code, including a
/// code for a stateless person and the EEA or Swiss codes (<c>NO</c>, <c>IS</c>, <c>LI</c>, <c>CH</c>), counts as
/// extra-UE: the item is shown rather than silently omitted (docs/runbooks/rli.md, "Questura communication").</para>
/// </remarks>
public static class EuMemberStates
{
    /// <summary>ISO 3166-1 alpha-2 codes of the 27 EU member states.</summary>
    public static IReadOnlySet<string> Codes { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "AT", "BE", "BG", "CY", "CZ", "DE", "DK", "EE", "ES", "FI", "FR", "GR", "HR", "HU",
        "IE", "IT", "LT", "LU", "LV", "MT", "NL", "PL", "PT", "RO", "SE", "SI", "SK",
    };

    /// <summary>The citizenship code in its stored form: trimmed and upper-case.</summary>
    public static string NormalizeCode(string? citizenship) => (citizenship ?? string.Empty).Trim().ToUpperInvariant();

    /// <summary>True when <paramref name="citizenship"/> is the code of an EU member state (case and spaces ignored).</summary>
    public static bool IsEuCitizenship(string? citizenship) => Codes.Contains(NormalizeCode(citizenship));
}
