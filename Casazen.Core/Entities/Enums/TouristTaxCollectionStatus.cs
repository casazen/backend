namespace Casazen.Core.Entities.Enums;

/// <summary>
/// Where the collection of the tourist tax of a stay stands (E25/F01/US01/T01, NF-119, decision O12), kept on <see cref="StayTouristTax.CollectionStatus"/>.
/// The stored value is the last one a write of the collection saved; what a reader shows is the effective one that
/// <c>TouristTaxCollectionRules.Evaluate</c> derives from it and from the stay as it is now (a no-show, for one, is never due). Stored as an integer:
/// a member is never renumbered.
/// </summary>
public enum TouristTaxCollectionStatus
{
    ToCollect = 0,

    PaidOnline = 1,

    PaidAtProperty = 2,

    CollectedByChannel = 3,

    IncludedInBookingPayment = 4,

    NotDue = 5,
}
