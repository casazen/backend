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

    public Dictionary<string, string[]> RequiredDocuments { get; set; } = new()
    {
        ["default"] = ["CinCertificate", "SafetyCompliance"],
        ["LOM"] = ["CinCertificate", "SafetyCompliance", "Ape"],
        ["LAZ"] = ["CinCertificate", "SafetyCompliance", "PropertyLicense"],
    };
}
