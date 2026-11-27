using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;

namespace Casazen.Core.Services;

/// <summary>Stable <c>code</c> values of the rooms of a property (NF-34): part of the API contract, never rename one.</summary>
public static class PropertyRoomErrorCodes
{
    /// <summary>422: the rooms break a rule (too many, a bed count out of range, no bed at all...); the message says which.</summary>
    public const string RoomsInvalid = "property_rooms_invalid";

    /// <summary>404: a room that is not one of this property (a room id in the rooms to save, or in the room of a photo).</summary>
    public const string RoomNotFound = "property_room_not_found";
}

/// <summary>A room as the host sends it: <paramref name="Id"/> is the room already stored that this one updates, <c>null</c> for a new room.</summary>
/// <param name="Id">The id of a room of the same property; one the property does not have is a 404 (<see cref="PropertyRoomErrorCodes.RoomNotFound"/>).</param>
/// <param name="Type">What the room is.</param>
/// <param name="Beds">Its beds, one entry for each kind (a kind sent twice is added up); <c>null</c> or empty = no bed.</param>
public sealed record RoomInput(Guid? Id, PropertyRoomType Type, IReadOnlyList<RoomBed>? Beds);

/// <summary>A room as it is stored, in the order the host gave.</summary>
public sealed record PropertyRoomView(Guid Id, int Position, PropertyRoomType Type, IReadOnlyList<RoomBed> Beds);

/// <summary>The rooms after the rules: what is stored, and what the property counters become.</summary>
/// <param name="Rooms">The rooms in order, each with its beds one entry for each kind, in the order of <see cref="BedType"/>.</param>
/// <param name="Bedrooms">How many of them are <see cref="PropertyRoomType.Bedroom"/>: the new value of <c>Property.Bedrooms</c>.</param>
/// <param name="Beds">How many beds in all: the new value of <c>Property.Beds</c>.</param>
public sealed record NormalizedRooms(IReadOnlyList<RoomInput> Rooms, int Bedrooms, int Beds);

/// <summary>
/// The rules of the rooms of a unit (NF-34, AC-NF34-1): at most <see cref="PropertyRoom.MaxPerProperty"/> rooms, 1 to
/// <see cref="PropertyRoom.MaxBedsOfAKind"/> beds of a kind in a room, and at least one bed in all, within the bounds of the beds of a property
/// (<see cref="PropertyHouseRules.MinBeds"/> to <see cref="PropertyHouseRules.MaxBeds"/>). One place for the service and the tests.
/// </summary>
public static class PropertyRoomRules
{
    /// <summary>
    /// The rooms as they will be stored, and the counters of the property they give. Refuses the whole list at the first rule broken: nothing is
    /// half-valid.
    /// </summary>
    /// <exception cref="DomainRuleException">422 <see cref="PropertyRoomErrorCodes.RoomsInvalid"/>, with the message of the rule that broke.</exception>
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

            // One entry for each kind of bed: a kind sent twice is one kind with the beds added up.
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
    /// <summary>The rooms in order; <c>null</c> when the property does not exist for the caller. A property with no room gives an empty list.</summary>
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
