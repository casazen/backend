namespace Casazen.Core.Entities.Enums;

/// <summary>
/// What a <see cref="Casazen.Core.Entities.SupplierBusyWindow"/> does to the supplier's agenda (SP-03). The values are
/// stored: never reorder or renumber, only append.
/// </summary>
public enum SupplierBusyWindowKind
{
    /// <summary>The supplier blocks these hours by hand: no slot overlaps them.</summary>
    Block = 0,

    /// <summary>
    /// The supplier opens these hours on top of the weekly working hours (an extra band for one day, for example a
    /// Saturday afternoon). It never takes a day out of a holiday or a closed day.
    /// </summary>
    ExtraOpening = 1,

    /// <summary>An engagement of the supplier's own calendar (the iCal feed, SP-05): no slot overlaps it.</summary>
    External = 2,
}
