namespace Casazen.Core.Entities.Enums;

/// <summary>
/// Who issues the fiscal documents of an organization (<see cref="OrgFiscalIssuerProfile.IssuerMode"/>, FIS-02). Since the decision of the PO of
/// 2026-11-14 (P1, P2) CasaZen is always the issuer: <see cref="CasaZen"/> is the only mode the API accepts, every stored profile was moved to it by the
/// migration <c>AddSoleIssuerModel</c>, and the other two members are kept only so the numbers of old rows stay readable. Stored as a number: a value
/// never changes, a new one takes the next number.
/// </summary>
public enum DocumentIssuerMode
{
    /// <summary>The channel manager issued the documents and CasaZen read them. Retired: CasaZen issues and an Octorate document never blocks it.</summary>
    [Obsolete("Retired 2026-11-14, kept for stored rows: CasaZen is always the issuer.")]
    Octorate = 1,

    /// <summary>CasaZen issues the documents of every booking, whatever its source (formerly «direct bookings only», the same number).</summary>
    CasaZen = 2,

    /// <summary>Nobody issued in CasaZen and the accountant took care of the documents. Retired: the accountant only receives the exports.</summary>
    [Obsolete("Retired 2026-11-14, kept for stored rows: the accountant only receives the exports.")]
    Accountant = 3,
}
