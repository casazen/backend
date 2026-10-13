namespace Casazen.Core.Entities.Enums;

/// <summary>
/// How a property is let (PM-01, decisions D16 and D19): the mode is <b>exclusive</b>, a property is either let for short
/// stays (the booking site, the calendar, the CIN and the compliance of D.L. 145/2023) or let long-term (leases), never
/// both at once. Stored as an integer in <c>Properties.RentalMode</c>: <b>append only</b>, never renumber or reuse a value
/// (a future <c>Both = 2</c> is left room for).
/// </summary>
/// <remarks>
/// Before PM-01 the only marker of a long-term property was implicit: no guests and no nightly rate
/// (<c>MaxGuests = 0</c> and <c>NightlyRate = 0</c>, audit A7-06). The rule that turns that marker into a mode when a
/// property is created without one is <see cref="Casazen.Core.Services.PropertyRentalModeRules.ResolveForCreation"/>;
/// the data migration <c>AddPropertyRentalMode</c> applies D19 to the existing rows. Runbook:
/// <c>docs/runbooks/property-rental-mode.md</c>.
/// </remarks>
public enum RentalMode
{
    /// <summary>Short stays: the default, the mode of every property that existed before PM-01 unless D19 says otherwise.</summary>
    Short = 0,

    /// <summary>Long-term leases: not published, not bookable and outside the short-rent compliance and CIN alerts.</summary>
    Long = 1,
}
