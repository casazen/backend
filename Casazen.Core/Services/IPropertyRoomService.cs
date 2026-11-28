using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;

namespace Casazen.Core.Services;

public static class PropertyRoomErrorCodes
{
    public const string RoomsInvalid = "property_rooms_invalid";

    public const string RoomNotFound = "property_room_not_found";
}

public sealed record RoomInput(Guid? Id, PropertyRoomType Type, IReadOnlyList<RoomBed>? Beds);

public sealed record PropertyRoomView(Guid Id, int Position, PropertyRoomType Type, IReadOnlyList<RoomBed> Beds);

public sealed record NormalizedRooms(IReadOnlyList<RoomInput> Rooms, int Bedrooms, int Beds);

public static class PropertyRoomRules
{
    public static NormalizedRooms Normalize(IReadOnlyList<RoomInput>? rooms)
    {
        rooms ??= [];
        if (rooms.Count > PropertyRoom.MaxPerProperty)
            throw Invalid("PropertyRoomsTooMany", PropertyRoom.MaxPerProperty);

        var seen = new HashSet<Guid>();
        var normalized = new List<RoomInput>(rooms.Count);
        foreach (var room in rooms)
        {
            if (!Enum.IsDefined(room.Type))
                throw Invalid("PropertyRoomsTypeInvalid");

            if (room.Id is { } id && !seen.Add(id))
                throw Invalid("PropertyRoomsDuplicateId");

            var counts = new SortedDictionary<BedType, int>();
            foreach (var bed in room.Beds ?? [])
            {
                if (bed is null || !Enum.IsDefined(bed.Type))
                    throw Invalid("PropertyRoomsBedTypeInvalid");

                if (bed.Count < PropertyRoom.MinBedsOfAKind || bed.Count > PropertyRoom.MaxBedsOfAKind)
                    throw Invalid("PropertyRoomsBedCountInvalid", PropertyRoom.MinBedsOfAKind, PropertyRoom.MaxBedsOfAKind);

                var count = counts.GetValueOrDefault(bed.Type) + bed.Count;
                if (count > PropertyRoom.MaxBedsOfAKind)
                    throw Invalid("PropertyRoomsBedCountInvalid", PropertyRoom.MinBedsOfAKind, PropertyRoom.MaxBedsOfAKind);

                counts[bed.Type] = count;
            }

            normalized.Add(new RoomInput(room.Id, room.Type, [.. counts.Select(pair => new RoomBed(pair.Key, pair.Value))]));
        }

        var beds = normalized.Sum(room => RoomBeds.Total(room.Beds ?? []));
        if (beds < PropertyHouseRules.MinBeds || beds > PropertyHouseRules.MaxBeds)
            throw Invalid("PropertyRoomsBedsTotalInvalid", PropertyHouseRules.MinBeds, PropertyHouseRules.MaxBeds);

        return new NormalizedRooms(normalized, normalized.Count(room => room.Type == PropertyRoomType.Bedroom), beds);
    }

    private static DomainRuleException Invalid(string messageKey, params object[] args) =>
        new(PropertyRoomErrorCodes.RoomsInvalid, messageKey, args);
}

/// <summary>
/// The rooms of a unit with their beds (NF-34, AC-NF34-1): the detail the channels ask for behind the counters of the property. Reads and writes
/// one property of the caller's org (the tenant filter hides another org's property, so it is simply not found); the permission to do so is the
/// caller's to check (<c>PropertyListingDataController</c>).
/// </summary>
public interface IPropertyRoomService
{
    Task<IReadOnlyList<PropertyRoomView>?> GetAsync(Guid propertyId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces the rooms with <paramref name="rooms"/> (a room sent with the id of a stored one updates it and keeps its photos; a stored room not
    /// sent is removed, and the photos assigned to it lose the assignment) and sets <c>Property.Bedrooms</c> to the number of bedrooms and
    /// <c>Property.Beds</c> to the number of beds, in the same save. <c>MaxGuests</c>, <c>Bathrooms</c> and the area are not touched. Answers with
    /// the rooms as stored; <c>null</c> when the property does not exist for the caller.
    /// </summary>
    /// <exception cref="DomainRuleException">
    /// 422 <see cref="PropertyRoomErrorCodes.RoomsInvalid"/>, or <c>property_not_bookable_in_long_mode</c> (a long-rent property has no listing).
    /// Nothing is saved.
    /// </exception>
    /// <exception cref="NotFoundException"><see cref="PropertyRoomErrorCodes.RoomNotFound"/>: a room id that is not one of the property's.</exception>
    Task<IReadOnlyList<PropertyRoomView>?> ReplaceAsync(
        Guid propertyId,
        IReadOnlyList<RoomInput> rooms,
        CancellationToken cancellationToken = default);
}
