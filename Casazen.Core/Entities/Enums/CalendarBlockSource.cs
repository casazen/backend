namespace Casazen.Core.Entities.Enums;

/// <summary>Where a calendar block comes from. Stored as an integer: a member is appended with the next number, never renumbered.</summary>
public enum CalendarBlockSource
{
    ICalImport = 0,
    Manual = 1,

    /// <summary>
    /// The nights Octorate closes (stop sale, or no unit available on a day that is on sale) and that nothing else in CasaZen takes (no booking,
    /// no manual block, no iCal block), read from its calendar by the calendar sync (E02/F02/US01/T03, PO-16: inbound only). No feed: the key is
    /// <c>(PropertyId, ExternalUid)</c> (a unique filtered index), the UID is <c>octorate:{date of the first night}</c>. The host cannot remove
    /// it (it is not a manual block); it is never exported (no echo) and never turned into an OTA stay.
    /// </summary>
    Octorate = 2,
}
