namespace Casazen.Core.Entities.Enums;

/// <summary>
/// The fiscal regime of whoever issues the documents (<see cref="OrgFiscalIssuerProfile.DocumentRegime"/>, FIS-02): it decides which document the issuer
/// gives and the tax rule that goes with it (<c>FiscalDocuments:TaxRules</c>, chosen by country, regime and date). The first three are Italian, the last
/// two Swiss: the country of the profile decides which ones are valid (<c>FiscalDocumentRules.CountryOfRegime</c>). Stored as a number: a value never
/// changes, a new one takes the next number.
/// </summary>
public enum DocumentRegime
{
    /// <summary>Italy: a private person with no VAT number.</summary>
    PrivatePerson = 1,

    /// <summary>Italy: a VAT number in the ordinary regime.</summary>
    VatOrdinary = 2,

    /// <summary>Italy: a VAT number in the flat-rate regime (forfettario).</summary>
    VatFlatRate = 3,

    /// <summary>Switzerland: registered for the VAT (MWST), with its UID number and the suffix «IVA».</summary>
    SwissVatRegistered = 4,

    /// <summary>Switzerland: not registered for the VAT (below the threshold or exempt): its documents carry no VAT.</summary>
    SwissNotRegistered = 5,
}
