namespace Casazen.Core.Identity;

// E71/F01/US02/T02 (NF-190, NF-191, decision D11): what the coherence of a document says. Check names, results and field NAMES only: no value read on the document
// is ever in a report (the check of NF-190 is made on values, the report keeps what it found, never what it read), so a report can be kept, logged and sent.

/// <summary>
/// The coherence of the fields read on a document (D11: the reliability of a document is coherence only, a coherent photo can still show an altered document):
/// whether the MRZ and the visual zone agree on the fields that identify the holder, whether the dates are possible, whether the document is still valid on the
/// day of the stay, whether the check digits of the MRZ hold and whether the document agrees with what the guest typed. The checker <b>decides nothing</b> about the
/// outcome: the engine of the decision (E73) turns an identifying conflict and an invalid MRZ into a review and the codes into the guidance of the guest.
/// </summary>
/// <param name="Checks">
/// Every check made, in the order of <see cref="DocumentFormatChecks.All"/> (<c>mrz_check_digits</c>, <c>expiry</c>, <c>side_consistency</c>, <c>field_coherence</c>,
/// <c>booking_consistency</c>), each with its result: <c>unknown</c> is never a pass. A format that requires a check needs it to have at least one entry and every
/// entry <c>passed</c>.
/// </param>
/// <param name="IdentifyingConflict">
/// True when a field the policy calls identifying (surname, given names, birth date, document number) reads differently on the two sides (the MRZ and the visual
/// zone, or two printed sides): <c>side_consistency</c> failed. A field read on one side only is <c>unknown</c>, never a conflict.
/// </param>
/// <param name="Expired">True when the date of expiry is before the day of the stay: <c>expiry</c> failed.</param>
/// <param name="DatesValid">
/// False when a date is impossible (a birth in the future or more than 120 years before the stay, an issue after the expiry): <c>field_coherence</c> failed. A date
/// that was not read does not make it false (the entry is <c>unknown</c>).
/// </param>
/// <param name="MrzValid">
/// True only when the MRZ was read and every ICAO 9303 check digit holds: <c>mrz_check_digits</c> passed. False when a digit fails, and also when no MRZ was read
/// (nothing proves it, so nothing is assumed).
/// </param>
/// <param name="Codes">The guidance codes of <see cref="IdentityGuidanceCodes"/> the findings raise: <c>document_field_conflict</c> and <c>document_expired</c>.</param>
public sealed record DocumentCoherenceReport(
    IReadOnlyList<DocumentCoherenceCheck> Checks,
    bool IdentifyingConflict,
    bool Expired,
    bool DatesValid,
    bool MrzValid,
    IReadOnlyList<string> Codes);

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

    /// <summary>The check could not be made: a value is missing (it was not read, or its own check digit failed). Never a pass.</summary>
    public const string Unknown = "unknown";

    /// <summary>Every code, in this order.</summary>
    public static IReadOnlyList<string> All { get; } = [Passed, Failed, Unknown];
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
