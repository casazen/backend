namespace Casazen.Core.Entities.Enums;

/// <summary>
/// Lifecycle of a <see cref="Casazen.Core.Entities.SupplierServiceListing"/> in the supplier's price catalog (SP-02).
/// A listing is created as <see cref="Draft"/>, published with <c>POST api/supplier/services/{id}/publish</c> and taken
/// off with <c>.../pause</c>; it never goes back to <see cref="Draft"/>, so a draft was never public.
/// </summary>
public enum SupplierServiceListingStatus
{
    /// <summary>Being written: seen only by its supplier, may be incomplete (the wizard saves a draft at every step).</summary>
    Draft = 0,

    /// <summary>
    /// Published: on offer. It always meets the publication requirements (<c>SupplierServiceListingRules</c>), also after
    /// an edit. Whether it is shown on the public showcase also depends on the supplier being active (public reads, SP-09).
    /// </summary>
    Active = 1,

    /// <summary>Taken off by the supplier without losing it: it can be published again.</summary>
    Paused = 2,
}
