namespace Casazen.Core.Entities.Enums;

/// <summary>
/// The kind of a bed in a room (NF-34). Stored by name in the beds of <c>PropertyRooms.BedsJson</c>: <b>append only</b>, never rename or
/// renumber a member.
/// </summary>
public enum BedType
{
    /// <summary>Letto singolo.</summary>
    Single = 0,

    /// <summary>Letto matrimoniale.</summary>
    Double = 1,

    /// <summary>Letto queen size.</summary>
    Queen = 2,

    /// <summary>Letto king size.</summary>
    King = 3,

    /// <summary>Divano letto.</summary>
    SofaBed = 4,

    /// <summary>Letto a castello.</summary>
    Bunk = 5,

    /// <summary>Culla.</summary>
    Crib = 6,
}
