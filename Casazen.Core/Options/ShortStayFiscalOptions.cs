namespace Casazen.Core.Options;

/// <summary>
/// Fiscal rules of short-term rentals (locazioni brevi) used by the fiscal area (CO-18, A5-22). Every value comes from
/// <c>.claude/context/regulations/fiscale.md</c> § "CO-18" and carries its source, shown to the host next to the rule.
/// The defaults mirror <c>appsettings.json</c>; change the configuration, not the code, when the law changes.
/// </summary>
public class ShortStayFiscalOptions
{
    public const string SectionName = "ShortStayFiscal";

    /// <summary>
    /// Most apartments one taxpayer may let as short-term rentals in a tax year and keep the short-rental regime
    /// (cedolare secca, OTA withholding). From the next one the activity is presumed to be a business (fiscale.md C3, C4).
    /// </summary>
    public int MaxApartmentsPerTaxpayer { get; set; } = 2;

    /// <summary>First tax year of <see cref="MaxApartmentsPerTaxpayer"/> (until 2025 the limit was different).</summary>
    public int ThresholdFromTaxYear { get; set; } = 2026;

    public string ThresholdSource { get; set; } =
        "art. 1 c. 595 L. 178/2020, modificato da art. 1 c. 17 L. 199/2025 (dal periodo d'imposta 2026)";

    /// <summary>Longest stay, in nights, that is still a short-term rental (fiscale.md C1).</summary>
    public int MaxStayNights { get; set; } = 30;

    public string ShortStaySource { get; set; } = "art. 4 c. 1 D.L. 50/2017";

    /// <summary>General cedolare secca rate on short-term rental income, as a fraction (fiscale.md C2).</summary>
    public decimal CedolareRate { get; set; } = 0.26m;

    /// <summary>Reduced rate for the one unit the taxpayer designates per tax year, as a fraction (fiscale.md C2).</summary>
    public decimal CedolareReducedRate { get; set; } = 0.21m;

    public string CedolareSource { get; set; } = "art. 1 c. 63 L. 213/2023";

    /// <summary>Withholding of intermediaries and portals on short-term rentals, as a fraction (fiscale.md C7).</summary>
    public decimal OtaWithholdingRate { get; set; } = 0.21m;

    public string OtaWithholdingSource { get; set; } = "art. 4 c. 5 D.L. 50/2017";

    public bool IsValid() =>
        MaxApartmentsPerTaxpayer > 0
        && ThresholdFromTaxYear > 0
        && MaxStayNights > 0
        && IsRate(CedolareRate)
        && IsRate(CedolareReducedRate)
        && IsRate(OtaWithholdingRate)
        && !string.IsNullOrWhiteSpace(ThresholdSource)
        && !string.IsNullOrWhiteSpace(ShortStaySource)
        && !string.IsNullOrWhiteSpace(CedolareSource)
        && !string.IsNullOrWhiteSpace(OtaWithholdingSource);

    private static bool IsRate(decimal rate) => rate is > 0m and < 1m;
}
