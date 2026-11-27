namespace Casazen.Core.Entities.Enums;

/// <summary>
/// Where the collection of the tourist tax of a stay stands (E25/F01/US01/T01, NF-119, decision O12), kept on <see cref="StayTouristTax.CollectionStatus"/>.
/// The stored value is the last one a write of the collection saved; what a reader shows is the effective one that
/// <c>TouristTaxCollectionRules.Evaluate</c> derives from it and from the stay as it is now (a no-show, for one, is never due). Stored as an integer:
/// a member is never renumbered.
/// </summary>
public enum TouristTaxCollectionStatus
{
    /// <summary>The host collects the tax and nothing was collected yet: the guest can pay it online when the organization offers it.</summary>
    ToCollect = 0,

    /// <summary>
    /// The guest paid it online, on the organization's own account: <see cref="StayTouristTax.CollectedAmount"/> in <see cref="StayTouristTax.Currency"/>,
    /// as the payment provider received it (never a figure of the client).
    /// </summary>
    PaidOnline = 1,

    /// <summary>The host recorded that the guest paid it at the property.</summary>
    PaidAtProperty = 2,

    /// <summary>The channel collects it and pays the comune: nothing to pay to the host, never a second charge.</summary>
    CollectedByChannel = 3,

    /// <summary>The price of the booking included it (<c>Booking.TouristTaxAmount</c>): the guest pays it with the booking.</summary>
    IncludedInBookingPayment = 4,

    /// <summary>Nothing is due: the stay was cancelled before any night or is a no-show, or every guest is exempt.</summary>
    NotDue = 5,
}
