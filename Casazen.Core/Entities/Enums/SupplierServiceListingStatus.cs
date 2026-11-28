namespace Casazen.Core.Entities.Enums;

/// <summary>
/// Lifecycle of a <see cref="Casazen.Core.Entities.SupplierServiceListing"/> in the supplier's price catalog (SP-02).
/// A listing is created as <see cref="Draft"/>, published with <c>POST api/supplier/services/{id}/publish</c> and taken
/// off with <c>.../pause</c>; it never goes back to <see cref="Draft"/>, so a draft was never public.
/// </summary>
public enum SupplierServiceListingStatus
{
    Draft = 0,

    Active = 1,

    Paused = 2,
}
