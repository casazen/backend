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

    public ComplianceStatusCheckOptions StatusCheck { get; set; } = new();

    public int IdentityRedHoursBeforeArrival { get; set; } = 24;

    /// <summary>
    /// How many days back the compliance status looks (E03/F02/US01/T03): the stays whose arrival is that recent get their traffic light
    /// (a stay older than that is neither listed green nor chased), and so do the closed months of the ISTAT and tourist tax periods. A
    /// proposal the product owner may change.
    /// </summary>
    public int StatusLookbackDays { get; set; } = 90;

    /// <summary>
    /// An <b>override</b> of the day of the following month by which the ISTAT communication of a month is due, per region (the three-letter code of the
    /// region, <c>LOM</c>, as <see cref="RequiredDocuments"/>): <c>Compliance__IstatDeadlineDays__LOM=10</c>. The deadline of a region comes from the ISTAT
    /// region table (<c>IstatRegionRules</c>, E21/F01/US01/T01, edited by the platform staff); a day set here wins over the table's, and it only moves the day:
    /// it cannot turn a region off (the staff do that, by setting the row to «unknown»). A value that is not a day of the month (1 to 31) is ignored, with a
    /// warning in the log, and the table's day stands. <b>Empty by default, and a region with no day here and none in the table is «to define»</b>: it
    /// produces no ISTAT item, because CasaZen never guesses a deadline (E03/F02/US01/T03).
    /// </summary>
    public Dictionary<string, int> IstatDeadlineDays { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public ComplianceArchiveOptions Archive { get; set; } = new();

    /// <summary>
    /// How many days before the deadline day of an ISTAT (or tourist tax) period the host is told it is approaching (stage 1), when nothing proves it
    /// was met. Default 3.
    /// </summary>
    public int PeriodWarningDays { get; set; } = 3;

    public int MaxPeriodReminders { get; set; } = 3;

    public int PeriodReminderDays { get; set; } = 3;

    public int PeriodAlertHourLocal { get; set; } = 9;

    public int ConnectionAlertDays { get; set; } = 7;

    public int ConnectionReminderDays { get; set; } = 2;

    public int TicinoArrivalAlertDelayHours { get; set; } = 2;

    public int TicinoArrivalAlertDefaultHourLocal { get; set; } = 18;

    public int TicinoDueWarningHours { get; set; } = 6;

    public int TicinoMaxOverdueReminders { get; set; } = 2;

    public int TicinoOverdueReminderHours { get; set; } = 24;
}

public class ComplianceStatusCheckOptions
{
    /// <summary>
    /// Email the host when the <b>first</b> evaluation of a property published before CO-06 suspends it (the recalculation
    /// of the historic properties, A5-36). Off by default: the product owner warns the hosts first, then may turn it on.
    /// Every later suspension (a CIN, document or checklist removed, the nightly check) always emails the host.
    /// </summary>
    public bool NotifyOnFirstCheck { get; set; }
}
