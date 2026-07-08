namespace Casazen.Core.Options;

public class ComplianceOptions
{
    public const string SectionName = "Compliance";

    public string CinGuidanceUrl { get; set; } = "https://www.bdsr.it/cin";

    public int CheckoutReminderHourLocal { get; set; } = 20;

    public int GdprRetentionYears { get; set; } = 7;

    public Dictionary<string, string[]> RequiredDocuments { get; set; } = new()
    {
        ["default"] = ["CinCertificate", "SafetyCompliance"],
        ["LOM"] = ["CinCertificate", "SafetyCompliance", "Ape"],
        ["LAZ"] = ["CinCertificate", "SafetyCompliance", "PropertyLicense"],
    };
}
