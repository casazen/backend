using System.Text.Json.Serialization;

namespace Casazen.Core.Identity;

// Unknown is never a pass: a check that could not be made leaves its flag null, not false, so an unread value cannot be taken for a good one.
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

    public string Summary(string check) => SummaryOf(Checks, check, null);

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

    private bool Satisfied(string check)
    {
        var summary = Summary(check);
        return summary == DocumentCoherenceResults.Passed
            || (summary == DocumentCoherenceResults.NotApplicable && string.Equals(check, DocumentFormatChecks.BookingConsistency, StringComparison.Ordinal));
    }

    private static string SummaryOf(IReadOnlyList<DocumentCoherenceCheck> checks, string check, string[]? onlyFields)
    {
        // not_applicable is neutral in the booking check only: anywhere else it is no pass.
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
                notTold = true;
        }

        if (!seen || notTold)
            return DocumentCoherenceResults.Unknown;

        return passed ? DocumentCoherenceResults.Passed : DocumentCoherenceResults.NotApplicable;
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

    public const string NotApplicable = "not_applicable";

    public static IReadOnlyList<string> All { get; } = [Passed, Failed, Unknown, NotApplicable];
}

public static class DocumentCoherenceFields
{
    public const string Names = "names";

    public static IReadOnlyList<string> All { get; } = [Names];
}
