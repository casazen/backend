using Casazen.Core.Identity;

namespace Casazen.Infrastructure.Identity;

/// <summary>
/// The coherence of a document summed up for the decision (<see cref="IdentityDocumentCoherence"/>) from its report (<see cref="DocumentCoherenceReport"/>): the one
/// rule the closure of the presence and the qualification of a segment share (IDENT-DECLARED, NF-190, D11), so the two cannot read the same report differently. Pure: no
/// state, no I/O, no log, and it never sees a value read on the document (the report has none).
/// </summary>
/// <remarks>
/// In this order, the first that applies: an identifying conflict between the sides, a failed check digit of the MRZ, an expired document, a known conflict with what was
/// declared for the booking (<c>booking_consistency</c> failed, whatever the checks the format requires); then, for a format of the registry, a pass only when every check
/// the format requires holds (<see cref="DocumentCoherenceReport.AllPassed"/>: <c>booking_consistency</c> may be <c>not_applicable</c> when nothing was declared, no other
/// check may). Anything else, a format the registry does not hold included, is <see cref="IdentityDocumentCoherence.Unknown"/>: never a pass.
/// </remarks>
public static class IdentityDocumentCoherences
{
    /// <summary>The coherence of <paramref name="report"/> for <paramref name="format"/> (null when the registry does not hold it: never a pass).</summary>
    public static IdentityDocumentCoherence Of(DocumentCoherenceReport report, DocumentFormat? format)
    {
        ArgumentNullException.ThrowIfNull(report);

        if (report.IdentifyingConflict == true)
            return IdentityDocumentCoherence.IdentifyingConflict;

        if (report.Summary(DocumentFormatChecks.MrzCheckDigits) == DocumentCoherenceResults.Failed)
            return IdentityDocumentCoherence.MrzInvalid;

        if (report.Expired == true)
            return IdentityDocumentCoherence.Expired;

        if (report.Summary(DocumentFormatChecks.BookingConsistency) == DocumentCoherenceResults.Failed)
            return IdentityDocumentCoherence.BookingConflict;

        if (format is null)
            return IdentityDocumentCoherence.Unknown;

        return report.AllPassed(format.RequiredChecks) ? IdentityDocumentCoherence.Passed : IdentityDocumentCoherence.Unknown;
    }
}
