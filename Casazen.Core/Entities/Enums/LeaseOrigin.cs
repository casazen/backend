namespace Casazen.Core.Entities.Enums;

/// <summary>
/// How a lease came to be in CasaZen (LR-12, decision D2 of the long-term area interview). Fixed when the draft is created
/// and never changed. Stored as the number: append only.
/// </summary>
public enum LeaseOrigin
{
    /// <summary>The contract is prepared in CasaZen (the guided wizard, or the complete creation of <c>POST api/leases</c>): the template, the review and the download for signature apply.</summary>
    Created = 0,

    /// <summary>
    /// A contract already signed outside CasaZen that the landlord records (LR-13). It needs no template, no review and no
    /// download for signature: steps 5 and 6 of the flow do not apply to it.
    /// </summary>
    Imported = 1,
}
