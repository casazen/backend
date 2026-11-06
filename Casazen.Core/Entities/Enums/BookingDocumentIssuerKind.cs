namespace Casazen.Core.Entities.Enums;

/// <summary>
/// Who issues the documents of one booking (<see cref="BookingDocumentIssuer.Issuer"/>, FIS-02, E06/F02/US02/T01). A booking has one issuer, never two.
/// Stored as a number: a later task appends a value with the next number.
/// </summary>
public enum BookingDocumentIssuerKind
{
    /// <summary>Octorate issued, or issues, the documents of the booking (also by hand in its panel): CasaZen only reads them.</summary>
    Octorate = 1,

    /// <summary>CasaZen issues the documents of the booking.</summary>
    CasaZen = 2,
}
