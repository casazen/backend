namespace Casazen.Core.Entities.Enums;

/// <summary>
/// What a change of the calendar is (E08/F05/US01/T01, NF-36): the unit the host sees in the list of the changes it sent to the channel
/// manager. Stored as an integer; append-only, a number is never reused.
/// </summary>
public enum CalendarChangeKind
{
    /// <summary>The price or the rules of some days of one property, written by the host from the calendar. Stored as 1.</summary>
    DayRules = 1,

    /// <summary>Days of one property that the host closed (or opened again). Stored as 2.</summary>
    Block = 2,

    /// <summary>The same change applied to several properties at once, through the guided layer. Stored as 3.</summary>
    Bulk = 3,
}
