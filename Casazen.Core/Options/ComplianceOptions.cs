namespace Casazen.Core.Options;

public class ComplianceOptions
{
    public const string SectionName = "Compliance";

    /// <summary>
    /// Official BDSR portal page of the Ministero del Turismo (see <c>.claude/context/regulations/cin.md</c>).
    /// </summary>
    public const string DefaultCinGuidanceUrl = "https://www.ministeroturismo.gov.it/banca-dati-strutture-ricettive/";

    public string CinGuidanceUrl { get; set; } = DefaultCinGuidanceUrl;

    public int CheckoutReminderHourLocal { get; set; } = 20;

    public int GdprRetentionYears { get; set; } = 7;

    /// <summary>
    /// Documents required for the activation, per region. No safety certificate: D.L. 145/2023 art. 13-ter asks for none
    /// (proofs are optional on the safety checklist, CO-07).
    /// </summary>
    public Dictionary<string, string[]> RequiredDocuments { get; set; } = new()
    {
        ["default"] = ["CinCertificate"],
        ["LOM"] = ["CinCertificate", "Ape"],
        ["LAZ"] = ["CinCertificate", "PropertyLicense"],
    };
}
