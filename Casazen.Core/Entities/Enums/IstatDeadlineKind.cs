namespace Casazen.Core.Entities.Enums;

/// <summary>
/// How often the portal of a region wants the ISTAT movement (E21/F01/US01/T01). Only <see cref="Monthly"/> carries a day (<c>DeadlineDay</c>, the day of the
/// following month) and only a month with that day becomes an ISTAT period item of the cockpit: a deadline is never guessed. Stored as an integer: the
/// values are explicit and never renumbered.
/// </summary>
public enum IstatDeadlineKind
{
    Unknown = 0,

    Daily = 1,

    Monthly = 2,
}
