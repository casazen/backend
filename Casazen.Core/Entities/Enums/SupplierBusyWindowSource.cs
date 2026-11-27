namespace Casazen.Core.Entities.Enums;

/// <summary>
/// Who wrote a <see cref="Casazen.Core.Entities.SupplierBusyWindow"/> (SP-03): the supplier by hand
/// (<c>api/supplier/availability/blocks</c>) or the sync of the supplier's iCal feed (SP-05), which manages only its own
/// windows. The same split as <see cref="SupplierAvailabilitySource"/> for the days. The values are stored: never reorder
/// or renumber, only append.
/// </summary>
public enum SupplierBusyWindowSource
{
    Manual = 0,

    ICalFeed = 1,
}
