namespace Casazen.Core.Entities.Enums;

/// <summary>
/// How often the portal of a region wants the ISTAT movement (E21/F01/US01/T01). Only <see cref="Monthly"/> carries a day (<c>DeadlineDay</c>, the day of the
/// following month) and only a month with that day becomes an ISTAT period item of the cockpit: a deadline is never guessed. Stored as an integer: the
/// values are explicit and never renumbered.
/// </summary>
public enum IstatDeadlineKind
{
    /// <summary>Not stated by the source («da verificare»): no period item, no deadline shown.</summary>
    Unknown = 0,

    /// <summary>The portal wants the movement every day. It has no day of the following month, so it raises no monthly period item.</summary>
    Daily = 1,

    /// <summary>The movement of a month is due by a day of the following month (<c>DeadlineDay</c>, 1 to 31).</summary>
    Monthly = 2,
}
