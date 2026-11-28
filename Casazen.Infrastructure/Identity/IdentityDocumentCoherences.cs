using Casazen.Core.Identity;

namespace Casazen.Infrastructure.Identity;

// One rule from the report to the decision, shared by the closure and the qualification runner, so they cannot read a report differently.
public static class IdentityDocumentCoherences
{
    public static IdentityDocumentCoherence Of(DocumentCoherenceReport report, DocumentFormat? format)
    {
        ArgumentNullException.ThrowIfNull(report);

        if (report.IdentifyingConflict == true)
            return IdentityDocumentCoherence.IdentifyingConflict;

        if (report.Summary(DocumentFormatChecks.MrzCheckDigits) == DocumentCoherenceResults.Failed)
            return IdentityDocumentCoherence.MrzInvalid;

        if (report.Expired == true)
            return IdentityDocumentCoherence.Expired;

        // A known conflict does not depend on the checks the format lists: the format cannot be a way round it.
        var booking = report.Summary(DocumentFormatChecks.BookingConsistency);
        if (booking == DocumentCoherenceResults.Failed)
            return IdentityDocumentCoherence.BookingConflict;

        if (format is null || !report.AllPassed(format.RequiredChecks))
            return IdentityDocumentCoherence.Unknown;

        return booking == DocumentCoherenceResults.NotApplicable ? IdentityDocumentCoherence.PassedWithoutBooking : IdentityDocumentCoherence.Passed;
    }
}
