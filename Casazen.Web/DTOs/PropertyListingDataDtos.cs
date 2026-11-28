using System.ComponentModel.DataAnnotations;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;

namespace Casazen.Web.DTOs;

public sealed class PropertyRoomsRequest
{
    [MaxLength(100, ErrorMessage = "PropertyListingDataPayloadTooLong")]
    public List<PropertyRoomItem>? Rooms { get; set; }

    public IReadOnlyList<RoomInput> ToInput() => [.. (Rooms ?? []).Select(room => room.ToInput())];
}

public sealed class PropertyRoomItem
{
    public Guid? Id { get; set; }

    public PropertyRoomType Type { get; set; }

    [MaxLength(50, ErrorMessage = "PropertyListingDataPayloadTooLong")]
    public List<RoomBedItem>? Beds { get; set; }

    public RoomInput ToInput() => new(Id, Type, Beds?.Select(bed => new RoomBed(bed.Type, bed.Count)).ToList());
}

public sealed class RoomBedItem
{
    public BedType Type { get; set; }

    public int Count { get; set; }
}

public sealed class StayDiscountsRequest
{
    [MaxLength(100, ErrorMessage = "PropertyListingDataPayloadTooLong")]
    public List<StayDiscountItem>? Discounts { get; set; }

    public IReadOnlyList<StayDiscountTier> ToInput() => [.. (Discounts ?? []).Select(tier => new StayDiscountTier(tier.MinNights, tier.PercentOff))];
}

public sealed class StayDiscountItem
{
    public int MinNights { get; set; }

    public decimal PercentOff { get; set; }
}
