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

    /// <summary>
    /// The identity of the guest who leaves the document (CHK-11, NF-50) turns red when the arrival is within this many hours and the
    /// verification is not passed (or an operator has to decide); before that the item is yellow while the selfie is awaited
    /// (E03/F02/US01/T03, AC-NF50-8). A proposal the product owner may change.
    /// </summary>
    public int IdentityRedHoursBeforeArrival { get; set; } = 24;

    /// <summary>
    /// How many days back the compliance status looks (E03/F02/US01/T03): the stays whose arrival is that recent get their traffic light
    /// (a stay older than that is neither listed green nor chased), and so do the closed months of the ISTAT and tourist tax periods. A
    /// proposal the product owner may change.
    /// </summary>
    public int StatusLookbackDays { get; set; } = 90;

    /// <summary>
    /// The day of the following month by which the ISTAT communication of a month is due, per region (the three-letter code of the
    /// region, <c>LOM</c>, as <see cref="RequiredDocuments"/>): <c>Compliance__IstatDeadlineDays__LOM=10</c>. <b>Empty by default, and a
    /// region that is not here is «to define»</b>: it produces no ISTAT item, because CasaZen never guesses a deadline (E03/F02/US01/T03).
    /// </summary>
    public Dictionary<string, int> IstatDeadlineDays { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The archive of the proofs, its uploads, downloads and exports (<c>Compliance:Archive</c>, E03/F02/US03/T01).</summary>
    public ComplianceArchiveOptions Archive { get; set; } = new();

    // E03/F02/US02/T01 (NF-52, CHK-12): the thresholds of the alerts about a period and about the connection. Proposals the product owner may
    // change; the alerts run only with Features:ComplianceTrafficLight on, and the defaults are documented in docs/TECHNICAL.md.

    /// <summary>
    /// How many days before the deadline day of an ISTAT (or tourist tax) period the host is told it is approaching (stage 1), when nothing proves it
    /// was met. Default 3.
    /// </summary>
    public int PeriodWarningDays { get; set; } = 3;

    /// <summary>How many reminders follow the «overdue» alert of a period (stage 2). 0 disables them. Default 3.</summary>
    public int MaxPeriodReminders { get; set; } = 3;

    /// <summary>Days between the reminders of an overdue period (at least 1). Default 3.</summary>
    public int PeriodReminderDays { get; set; } = 3;

    /// <summary>Hour (Europe/Rome) at which the alerts of a period are sent. Default 9.</summary>
    public int PeriodAlertHourLocal { get; set; } = 9;

    /// <summary>
    /// The police send of the channel manager of a property that is not active is alerted when a stay arrives within this many days. Default 7.
    /// </summary>
    public int ConnectionAlertDays { get; set; } = 7;

    /// <summary>Days after the first alert about an inactive connection at which the second and last one is sent (at least 1). Default 2.</summary>
    public int ConnectionReminderDays { get; set; } = 2;
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
