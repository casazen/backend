namespace Casazen.Core.Entities.Enums;

/// <summary>
/// Who set a day of <see cref="Casazen.Core.Entities.SupplierAvailability"/> (SU-15). The iCal sync of the supplier
/// manages only its own days: it frees the <see cref="ICalFeed"/> days that left the feed and never touches a
/// <see cref="Manual"/> closure.
/// </summary>
public enum SupplierAvailabilitySource
{
    /// <summary>Set by the supplier (availability page), or written before SU-15 (source unknown, kept as manual).</summary>
    Manual = 0,

    /// <summary>Busy day of the supplier's iCal feed, written by the sync.</summary>
    ICalFeed = 1,
}
