using System.ComponentModel.DataAnnotations;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;

namespace Casazen.Web.DTOs;

/// <summary>
/// Body of <c>PUT /api/properties/{id}/rooms</c> (NF-34): the whole list of the rooms of the unit, which replaces the stored one. A room
/// sent with the <c>id</c> of a stored room updates it (its photos keep their room); a stored room not sent is removed, and its photos lose the
/// assignment. At most 20 rooms, 1 to 10 beds of a kind in a room, and at least one bed in all.
/// </summary>
public sealed class PropertyRoomsRequest
{
    /// <summary>The rooms, in the order they are listed.</summary>
    [MaxLength(100, ErrorMessage = "PropertyListingDataPayloadTooLong")]
    public List<PropertyRoomItem>? Rooms { get; set; }

    public IReadOnlyList<RoomInput> ToInput() => [.. (Rooms ?? []).Select(room => room.ToInput())];
}

/// <summary>One room of the unit.</summary>
public sealed class PropertyRoomItem
{
    /// <summary>The id of a room of this property that this one updates; left out or <c>null</c> = a new room.</summary>
    public Guid? Id { get; set; }

    /// <summary><c>Bedroom</c>, <c>LivingRoom</c>, <c>Studio</c> or <c>Other</c>.</summary>
    public PropertyRoomType Type { get; set; }

    /// <summary>The beds of the room, one entry for each kind; left out or empty = no bed.</summary>
    [MaxLength(50, ErrorMessage = "PropertyListingDataPayloadTooLong")]
    public List<RoomBedItem>? Beds { get; set; }

    public RoomInput ToInput() => new(Id, Type, Beds?.Select(bed => new RoomBed(bed.Type, bed.Count)).ToList());
}

/// <summary>A kind of bed in a room and how many of them.</summary>
public sealed class RoomBedItem
{
    /// <summary><c>Single</c>, <c>Double</c>, <c>Queen</c>, <c>King</c>, <c>SofaBed</c>, <c>Bunk</c> or <c>Crib</c>.</summary>
    public BedType Type { get; set; }

    /// <summary>How many beds of this kind, 1 to 10.</summary>
    public int Count { get; set; }
}

/// <summary>
/// Body of <c>PUT /api/properties/{id}/stay-discounts</c> (NF-35): the whole list of the length-of-stay discounts of the property, which replaces
/// the stored one (an empty list removes them all). At most 5 tiers, each with its own minimum of nights.
/// </summary>
public sealed class StayDiscountsRequest
{
    /// <summary>The tiers, in any order; they are stored and answered in order of nights.</summary>
    [MaxLength(100, ErrorMessage = "PropertyListingDataPayloadTooLong")]
    public List<StayDiscountItem>? Discounts { get; set; }

    public IReadOnlyList<StayDiscountTier> ToInput() => [.. (Discounts ?? []).Select(tier => new StayDiscountTier(tier.MinNights, tier.PercentOff))];
}

/// <summary>One tier of the length-of-stay discount.</summary>
public sealed class StayDiscountItem
{
    /// <summary>The fewest nights of a stay this tier applies to, 2 to 365; unique for the property.</summary>
    public int MinNights { get; set; }

    /// <summary>The share of the lodging taken off, above 0 and at most 100, two decimals. The cleaning fee and the tourist tax are never reduced.</summary>
    public decimal PercentOff { get; set; }
}
