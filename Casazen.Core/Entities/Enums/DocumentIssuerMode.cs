namespace Casazen.Core.Entities.Enums;

/// <summary>
/// Who issues the fiscal documents of an organization (<see cref="OrgFiscalIssuerProfile.IssuerMode"/>, FIS-02, PO-05, E06/F02/US02/T01). A booking
/// never has two issuers: where Octorate issues, CasaZen reads and does not issue. Stored as a number: a later task appends a value with the next
/// number, an existing one never changes.
/// </summary>
public enum DocumentIssuerMode
{
    /// <summary>The channel manager issues the documents and CasaZen reads them (FIS-04). The recommended choice only when Octorate can issue every booking (D34).</summary>
    Octorate = 1,

    /// <summary>CasaZen issues, for the direct bookings only (the ones of the CasaZen site and of the agency's booking engine); never for an OTA booking.</summary>
    CasaZenDirectOnly = 2,

    /// <summary>Nobody issues here: the accountant takes care of the documents.</summary>
    Accountant = 3,
}
