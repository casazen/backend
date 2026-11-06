namespace Casazen.Core.Entities.Enums;

/// <summary>
/// The fiscal regime of whoever issues the documents (<see cref="OrgFiscalIssuerProfile.DocumentRegime"/>, FIS-02, E06/F02/US02/T01): it decides which
/// document the issuer needs and the rules that go with it (<c>FiscalDocumentRulesOptions</c>). CasaZen does not issue the invoice of an agency to its
/// owners, so there is no regime for it. Stored as a number: a later task appends a value with the next number.
/// </summary>
public enum DocumentRegime
{
    /// <summary>A private person with no VAT number.</summary>
    PrivatePerson = 1,

    /// <summary>A VAT number in the ordinary regime.</summary>
    VatOrdinary = 2,

    /// <summary>A VAT number in the flat-rate regime (forfettario).</summary>
    VatFlatRate = 3,
}
