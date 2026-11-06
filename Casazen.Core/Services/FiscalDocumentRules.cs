using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

/// <summary>
/// The rules that decide who can issue the documents of which booking and with which data (FIS-02, NF-82, E06/F02/US02/T02), in one place: whether a
/// booking is a direct one, and whether the profile of an issuer is complete enough for CasaZen to issue.
/// </summary>
public static class FiscalDocumentRules
{
    /// <summary>
    /// True for a direct booking: the one made on the booking site (<see cref="BookingSource.Direct"/>) or entered by the host
    /// (<see cref="BookingSource.Manual"/>); never for an OTA booking, whatever channel it came from (<see cref="FiscalCopy.IsOtaBookingSource"/>, the one
    /// definition). The bookings of the Octorate booking engine on the agency's site count as direct once the import maps them to a source that is not an
    /// OTA's (D34); until then they are treated as OTA, so CasaZen does not issue for them.
    /// </summary>
    public static bool IsDirectBooking(Booking booking)
    {
        ArgumentNullException.ThrowIfNull(booking);
        return IsDirectSource(booking.Source);
    }

    /// <summary>The same as <see cref="IsDirectBooking(Booking)"/> for the source alone.</summary>
    public static bool IsDirectSource(BookingSource source) =>
        source is BookingSource.Direct or BookingSource.Manual && !FiscalCopy.IsOtaBookingSource(source);

    /// <summary>
    /// The fields the issuer still has to give for CasaZen to issue its documents (<see cref="IssuerFields"/>), in the order of the wizard; empty when the
    /// profile is complete. The legal name, the structured address (the province too, in Italy) and the regime are always needed; for a regime with a VAT
    /// number the FatturaPA regime code (<paramref name="effectiveRegimeCode"/>, the profile's own or the rule's) and the VAT number of the organization;
    /// for a private person, who has no VAT number and no electronic invoice, the fiscal code of the organization.
    /// </summary>
    /// <param name="issuer">The profile of the issuer.</param>
    /// <param name="regime">The regime, once the issuer has said it.</param>
    /// <param name="effectiveRegimeCode">The regime code of FatturaPA that would be used.</param>
    /// <param name="orgVatNumber">The VAT number on the organization.</param>
    /// <param name="orgFiscalCode">The fiscal code on the organization.</param>
    public static IReadOnlyList<string> MissingIssuerFields(
        IssuerData issuer,
        DocumentRegime? regime,
        string? effectiveRegimeCode,
        string? orgVatNumber,
        string? orgFiscalCode)
    {
        ArgumentNullException.ThrowIfNull(issuer);

        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(issuer.LegalName))
            missing.Add(IssuerFields.LegalName);
        if (string.IsNullOrWhiteSpace(issuer.AddressStreet))
            missing.Add(IssuerFields.AddressStreet);
        if (string.IsNullOrWhiteSpace(issuer.PostalCode))
            missing.Add(IssuerFields.PostalCode);
        if (string.IsNullOrWhiteSpace(issuer.City))
            missing.Add(IssuerFields.City);
        if (string.Equals(issuer.CountryCode, "IT", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(issuer.Province))
            missing.Add(IssuerFields.Province);

        switch (regime)
        {
            case null:
                missing.Add(IssuerFields.Regime);
                break;
            case DocumentRegime.PrivatePerson:
                if (string.IsNullOrWhiteSpace(orgFiscalCode))
                    missing.Add(IssuerFields.FiscalCode);
                break;
            default:
                if (string.IsNullOrWhiteSpace(effectiveRegimeCode))
                    missing.Add(IssuerFields.FatturaPaRegimeCode);
                if (string.IsNullOrWhiteSpace(orgVatNumber))
                    missing.Add(IssuerFields.VatNumber);
                break;
        }

        return missing;
    }
}
