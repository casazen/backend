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

    /// <summary>Re-evaluation of the published properties (CO-06, docs/runbooks/compliance.md).</summary>
    public ComplianceStatusCheckOptions StatusCheck { get; set; } = new();
}

/// <summary>Section <c>Compliance:StatusCheck</c> (CO-06).</summary>
public class ComplianceStatusCheckOptions
{
    /// <summary>
    /// Email the host when the <b>first</b> evaluation of a property published before CO-06 suspends it (the recalculation
    /// of the historic properties, A5-36). Off by default: the product owner warns the hosts first, then may turn it on.
    /// Every later suspension (a CIN, document or checklist removed, the nightly check) always emails the host.
    /// </summary>
    public bool NotifyOnFirstCheck { get; set; }
}
