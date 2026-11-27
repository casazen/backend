namespace Casazen.Core.Entities.Enums;

/// <summary>
/// What a room of a unit is (NF-34): the rooms of a listing are the rooms with their beds. Stored as an integer in
/// <c>PropertyRooms.Type</c>, <b>append only</b>: never renumber a member.
/// </summary>
public enum PropertyRoomType
{
    /// <summary>Camera da letto. The number of these is what <c>Property.Bedrooms</c> counts.</summary>
    Bedroom = 0,

    /// <summary>Soggiorno.</summary>
    LivingRoom = 1,

    /// <summary>Monolocale: one room that is bedroom and living room together.</summary>
    Studio = 2,

    /// <summary>Any other room that has beds (a mezzanine, a veranda).</summary>
    Other = 3,
}
