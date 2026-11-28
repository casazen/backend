using System.Text.Json.Serialization;

namespace Casazen.Core.Identity;

/// <summary>
/// The coherence of the fields read on a document (D11: the reliability of a document is coherence only, a coherent photo can still show an altered document):
/// whether the MRZ and the visual zone agree on the fields that identify the holder, whether the dates are possible, whether the document is still valid on the
/// day of the stay, whether the check digits of the MRZ hold and whether the document agrees with what the guest typed. The checker <b>decides nothing</b> about the
/// outcome: the engine of the decision (E73) turns an identifying conflict and an invalid MRZ into a review and the codes into the guidance of the guest.
/// <para>
/// <b>Unknown is never a success.</b> A check that could not be made (a value was not read, or its own check digit failed) is <c>unknown</c>, and nothing in this
/// report says «ok» for it: the three flags that can be unknown are <c>bool?</c> (<c>null</c> is unknown, so a consumer cannot write <c>!report.Expired</c> and
/// take an unread date for a valid one), and <see cref="AllPassed"/> is the one answer to «may this document go on»: true only when every check the format
/// requires has entries and every entry passed.
/// </para>
/// </summary>
/// <param name="Checks">
/// Every check made, in the order of <see cref="DocumentFormatChecks.All"/> (<c>mrz_check_digits</c>, <c>expiry</c>, <c>side_consistency</c>, <c>field_coherence</c>,
/// <c>booking_consistency</c>), each with its result: <c>unknown</c> is never a pass. A format that requires a check needs it to have at least one entry and every
/// entry <c>passed</c> (<see cref="Summary"/>, <see cref="AllPassed"/>).
/// </param>
/// <param name="IdentifyingConflict">
/// The summary of <c>side_consistency</c>: <c>true</c> when a field the policy calls identifying (surname, given names, birth date, document number) reads
/// differently on the two sides (the MRZ and the visual zone, or two printed sides); <c>false</c> only when every one of those fields was compared and agrees;
/// <c>null</c> when none differs but one could not be compared (a field read on one side only is <c>unknown</c>, never a conflict, and never an agreement).
/// </param>
/// <param name="Expired">
/// The summary of <c>expiry</c>: <c>true</c> when the date of expiry is before the day of the stay; <c>false</c> only when a date of expiry was read and the document is
/// still valid that day; <c>null</c> when no date of expiry could be read.
/// </param>
/// <param name="DatesValid">
/// The summary of the date entries of <c>field_coherence</c> (birth, issue, and the expiry printed against the one of the MRZ): <c>false</c> when a date is impossible
/// (a birth in the future or more than 120 years before the stay, an issue after the expiry) or the printed expiry is not the one of the MRZ; <c>true</c> only when
/// every one of those entries passed; <c>null</c> when none failed but a date was not read (the issue date is only printed, so a card whose print was not read
/// is <c>null</c>, not valid). A sex or a nationality that differs from the MRZ is not a date: it fails <c>field_coherence</c> (<see cref="Summary"/>) and leaves
/// this flag alone.
/// </param>
/// <param name="MrzValid">
/// True only when the MRZ was read and every ICAO 9303 check digit holds: <c>mrz_check_digits</c> passed. False when a digit fails, and also when no MRZ was read
/// (nothing proves it, so nothing is assumed).
/// </param>
/// <param name="Codes">The guidance codes of <see cref="IdentityGuidanceCodes"/> the findings raise: <c>document_field_conflict</c> and <c>document_expired</c>.</param>
public sealed record DocumentCoherenceReport(
    IReadOnlyList<DocumentCoherenceCheck> Checks,
    bool? IdentifyingConflict,
    bool? Expired,
    bool? DatesValid,
    bool MrzValid,
    IReadOnlyList<string> Codes)
{
    public static IReadOnlyList<string> CarriedChecks { get; } =
    [
        DocumentFormatChecks.MrzCheckDigits, DocumentFormatChecks.Expiry, DocumentFormatChecks.SideConsistency, DocumentFormatChecks.FieldCoherence,
        DocumentFormatChecks.BookingConsistency,
    ];

    private static readonly string[] DateFields = [ZonedFieldNames.BirthDate, ZonedFieldNames.IssueDate, ZonedFieldNames.ExpiryDate];

    [JsonIgnore]
    public int UnknownCount => Checks.Count(entry => entry.Result == DocumentCoherenceResults.Unknown);

    /// <summary>
    /// The result of a whole check: <c>failed</c> when one entry failed, <c>passed</c> only when it has at least one entry and every entry passed, <c>unknown</c>
    /// otherwise (no entry, or an entry that could not be told). A name that is not a check of this report is <c>unknown</c>.
    /// </summary>
    public string Summary(string check) => SummaryOf(Checks, check, null);

    /// <summary>
    /// The one answer to «does the coherence of this document hold»: true only when <paramref name="requiredChecks"/> (the checks a format requires, for example
    /// <see cref="DocumentFormat.RequiredChecks"/>) names at least one check this report carries and every one of them is <c>passed</c> (<see cref="Summary"/>):
    /// an <c>unknown</c> entry is not a pass, an empty list requires nothing it can vouch for and is false, and a name that is no check of the plan never passes. The
    /// checks of the format that another report carries (<c>layout</c>, <c>portrait</c>: the geometry of the document) are not judged here.
    /// </summary>
    public bool AllPassed(IEnumerable<string> requiredChecks)
    {
        ArgumentNullException.ThrowIfNull(requiredChecks);

        var judged = requiredChecks
            .Where(check => CarriedChecks.Contains(check) || !DocumentFormatChecks.All.Contains(check))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        return judged.Count > 0 && judged.All(check => Summary(check) == DocumentCoherenceResults.Passed);
    }

    public static DocumentCoherenceReport FromChecks(IReadOnlyList<DocumentCoherenceCheck> checks)
    {
        ArgumentNullException.ThrowIfNull(checks);

        bool? conflict = Found(SummaryOf(checks, DocumentFormatChecks.SideConsistency, null));
        bool? expired = Found(SummaryOf(checks, DocumentFormatChecks.Expiry, null));
        bool? datesValid = Holds(SummaryOf(checks, DocumentFormatChecks.FieldCoherence, DateFields));
        var mrzValid = SummaryOf(checks, DocumentFormatChecks.MrzCheckDigits, null) == DocumentCoherenceResults.Passed;
        var codes = new List<string>();
        if (conflict == true)
            codes.Add(IdentityGuidanceCodes.DocumentFieldConflict);
        if (expired == true)
            codes.Add(IdentityGuidanceCodes.DocumentExpired);

        return new DocumentCoherenceReport(checks, conflict, expired, datesValid, mrzValid, codes);
    }

    private static string SummaryOf(IReadOnlyList<DocumentCoherenceCheck> checks, string check, string[]? onlyFields)
    {
        var seen = false;
        var notTold = false;
        foreach (var entry in checks)
        {
            if (entry.Check != check || (onlyFields is not null && (entry.Field is null || !onlyFields.Contains(entry.Field))))
                continue;

            seen = true;
            if (entry.Result == DocumentCoherenceResults.Failed)
                return DocumentCoherenceResults.Failed;

            if (entry.Result != DocumentCoherenceResults.Passed)
                notTold = true;
        }

        return seen && !notTold ? DocumentCoherenceResults.Passed : DocumentCoherenceResults.Unknown;
    }

    private static bool? Found(string summary) => summary switch
    {
        DocumentCoherenceResults.Failed => true,
        DocumentCoherenceResults.Passed => false,
        _ => null,
    };

    private static bool? Holds(string summary) => summary switch
    {
        DocumentCoherenceResults.Passed => true,
        DocumentCoherenceResults.Failed => false,
        _ => null,
    };
}

public sealed record DocumentCoherenceCheck(string Check, string Result, string? Field = null);

public static class DocumentCoherenceResults
{
    public const string Passed = "passed";

    public const string Failed = "failed";

    public const string Unknown = "unknown";

    public static IReadOnlyList<string> All { get; } = [Passed, Failed, Unknown];
}

public static class DocumentCoherenceFields
{
    public const string Names = "names";

    public static IReadOnlyList<string> All { get; } = [Names];
}
