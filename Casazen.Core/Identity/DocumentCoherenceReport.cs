using System.Text.Json.Serialization;

namespace Casazen.Core.Identity;

// E71/F01/US02/T02 (NF-190, NF-191, decision D11): what the coherence of a document says. Check names, results and field NAMES only: no value read on the document
// is ever in a report (the check of NF-190 is made on values, the report keeps what it found, never what it read), so a report can be kept, logged and sent.

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
/// <para>
/// <b>Nothing declared is no conflict</b> (IDENT-DECLARED, NF-190): a field of <c>booking_consistency</c> for which nothing was declared (a booking from a channel
/// has only a name, the guest page saves nothing typed before the identity step) has nothing to be compared with and is <c>not_applicable</c>: neutral, never a
/// conflict and never a pass by itself. A value declared and different from the document is still a conflict (<c>failed</c>), and a value declared that cannot be
/// compared is still <c>unknown</c>. <c>not_applicable</c> belongs to <c>booking_consistency</c> only: in any other check it is no pass (<see cref="Summary"/>), and it
/// satisfies the requirement of a format for that one check only (<see cref="AllPassed"/>).
/// </para>
/// </summary>
/// <param name="Checks">
/// Every check made, in the order of <see cref="DocumentFormatChecks.All"/> (<c>mrz_check_digits</c>, <c>expiry</c>, <c>side_consistency</c>, <c>field_coherence</c>,
/// <c>booking_consistency</c>), each with its result: <c>unknown</c> is never a pass, <c>not_applicable</c> (only in <c>booking_consistency</c>) is neutral. A format
/// that requires a check needs it to have at least one entry and every entry <c>passed</c>, or, for <c>booking_consistency</c>, <c>not_applicable</c>
/// (<see cref="Summary"/>, <see cref="AllPassed"/>).
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
    /// <summary>The checks of <see cref="DocumentFormatChecks"/> this report carries, in this order; the others (<c>layout</c>, <c>portrait</c>) are the geometry's.</summary>
    public static IReadOnlyList<string> CarriedChecks { get; } =
    [
        DocumentFormatChecks.MrzCheckDigits, DocumentFormatChecks.Expiry, DocumentFormatChecks.SideConsistency, DocumentFormatChecks.FieldCoherence,
        DocumentFormatChecks.BookingConsistency,
    ];

    /// <summary>The fields of <c>field_coherence</c> that are dates: the ones <see cref="DatesValid"/> summarizes.</summary>
    private static readonly string[] DateFields = [ZonedFieldNames.BirthDate, ZonedFieldNames.IssueDate, ZonedFieldNames.ExpiryDate];

    /// <summary>
    /// How many entries of the report are <c>unknown</c>: what could not be checked, never counted as a pass. A <c>not_applicable</c> entry is not one: nothing was
    /// declared to compare the document with.
    /// </summary>
    [JsonIgnore]
    public int UnknownCount => Checks.Count(entry => entry.Result == DocumentCoherenceResults.Unknown);

    /// <summary>
    /// The result of a whole check: <c>failed</c> when one entry failed; <c>passed</c> when at least one entry passed and every other entry passed or, in
    /// <c>booking_consistency</c> only, is <c>not_applicable</c>; <c>not_applicable</c> when every entry of <c>booking_consistency</c> is (nothing was declared to
    /// compare with); <c>unknown</c> otherwise (no entry, an entry that could not be told, or a <c>not_applicable</c> entry in any other check). A name that is not a
    /// check of this report is <c>unknown</c>.
    /// </summary>
    public string Summary(string check) => SummaryOf(Checks, check, null);

    /// <summary>
    /// The one answer to «does the coherence of this document hold»: true only when <paramref name="requiredChecks"/> (the checks a format requires, for example
    /// <see cref="DocumentFormat.RequiredChecks"/>) names at least one check this report carries, every one of them is <c>passed</c> (<see cref="Summary"/>) or, for
    /// <c>booking_consistency</c> only, <c>not_applicable</c>, and at least one of them passed: an <c>unknown</c> entry is not a pass, <c>not_applicable</c> satisfies
    /// no other check and never vouches for a document alone, an empty list requires nothing it can vouch for and is false, and a name that is no check of the plan
    /// never passes. The checks of the format that another report carries (<c>layout</c>, <c>portrait</c>: the geometry of the document) are not judged here.
    /// </summary>
    public bool AllPassed(IEnumerable<string> requiredChecks)
    {
        ArgumentNullException.ThrowIfNull(requiredChecks);

        var judged = requiredChecks
            .Where(check => CarriedChecks.Contains(check) || !DocumentFormatChecks.All.Contains(check))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        return judged.Count > 0
            && judged.All(Satisfied)
            && judged.Any(check => Summary(check) == DocumentCoherenceResults.Passed);
    }

    /// <summary>
    /// The report of a list of entries: the flags and the codes are derived from the entries and from nothing else, so no flag can say «ok» for a check whose entries
    /// are unknown, and the checker and a test double of a dependent slice speak the same contract.
    /// </summary>
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

    /// <summary>
    /// Whether <paramref name="check"/> holds as a format requires it: <c>passed</c>, or <c>not_applicable</c> for <c>booking_consistency</c> only (nothing was declared
    /// to compare the document with). <see cref="Summary"/> never gives <c>not_applicable</c> for another check; the name is tested all the same.
    /// </summary>
    private bool Satisfied(string check)
    {
        var summary = Summary(check);
        return summary == DocumentCoherenceResults.Passed
            || (summary == DocumentCoherenceResults.NotApplicable && string.Equals(check, DocumentFormatChecks.BookingConsistency, StringComparison.Ordinal));
    }

    private static string SummaryOf(IReadOnlyList<DocumentCoherenceCheck> checks, string check, string[]? onlyFields)
    {
        // Nothing declared to compare with is neutral in the check of the booking, and only there: in any other check it is no pass.
        var neutralAllowed = string.Equals(check, DocumentFormatChecks.BookingConsistency, StringComparison.Ordinal);
        var seen = false;
        var passed = false;
        var notTold = false;
        foreach (var entry in checks)
        {
            if (entry.Check != check || (onlyFields is not null && (entry.Field is null || !onlyFields.Contains(entry.Field))))
                continue;

            seen = true;
            if (entry.Result == DocumentCoherenceResults.Failed)
                return DocumentCoherenceResults.Failed;

            if (entry.Result == DocumentCoherenceResults.Passed)
                passed = true;
            else if (!(neutralAllowed && entry.Result == DocumentCoherenceResults.NotApplicable))
                notTold = true; // An unknown, a result nobody knows, or not_applicable outside the booking: not a pass either.
        }

        if (!seen || notTold)
            return DocumentCoherenceResults.Unknown;

        return passed ? DocumentCoherenceResults.Passed : DocumentCoherenceResults.NotApplicable;
    }

    /// <summary>A finding: true when the check failed, false when it passed, null when it could not be told.</summary>
    private static bool? Found(string summary) => summary switch
    {
        DocumentCoherenceResults.Failed => true,
        DocumentCoherenceResults.Passed => false,
        _ => null,
    };

    /// <summary>A property that holds: true when the check passed, false when it failed, null when it could not be told.</summary>
    private static bool? Holds(string summary) => summary switch
    {
        DocumentCoherenceResults.Passed => true,
        DocumentCoherenceResults.Failed => false,
        _ => null,
    };
}

/// <summary>One check of the report: which check, its result and the field it concerns.</summary>
/// <param name="Check">A code of <see cref="DocumentFormatChecks"/>.</param>
/// <param name="Result">A code of <see cref="DocumentCoherenceResults"/>.</param>
/// <param name="Field">
/// The NAME of the field the check concerns (a code of <see cref="ZonedFieldNames"/>, or one of <see cref="DocumentCoherenceFields"/> for the booking data), or
/// null when it concerns the document as a whole (<c>mrz_check_digits</c>).
/// </param>
public sealed record DocumentCoherenceCheck(string Check, string Result, string? Field = null);

/// <summary>The results of a check (<see cref="DocumentCoherenceCheck.Result"/>).</summary>
public static class DocumentCoherenceResults
{
    /// <summary>The check was made and holds.</summary>
    public const string Passed = "passed";

    /// <summary>The check was made and does not hold.</summary>
    public const string Failed = "failed";

    /// <summary>The check could not be made: a value is missing (it was not read, or its own check digit failed), or it cannot be compared. Never a pass.</summary>
    public const string Unknown = "unknown";

    /// <summary>
    /// There was nothing to compare with: no value was declared for this field of the booking (IDENT-DECLARED). Written in <c>booking_consistency</c> only, and neutral
    /// there: never a conflict and never a pass by itself; in any other check it is no pass (<see cref="DocumentCoherenceReport.Summary"/>).
    /// </summary>
    public const string NotApplicable = "not_applicable";

    /// <summary>Every code, in this order.</summary>
    public static IReadOnlyList<string> All { get; } = [Passed, Failed, Unknown, NotApplicable];
}

/// <summary>
/// The names of the fields of the <c>booking_consistency</c> check that are not a field of the document: <c>names</c> is the surname and the given names compared
/// as one, as <c>MrzCheckInConsistency</c> compares them with what the guest typed. The other three flags of that comparison are named as the field they compare
/// (<see cref="ZonedFieldNames.BirthDate"/>, <see cref="ZonedFieldNames.DocumentNumber"/>, <see cref="ZonedFieldNames.Nationality"/>).
/// </summary>
public static class DocumentCoherenceFields
{
    /// <summary>The surname and the given names, compared together.</summary>
    public const string Names = "names";

    /// <summary>Every code, in this order.</summary>
    public static IReadOnlyList<string> All { get; } = [Names];
}
