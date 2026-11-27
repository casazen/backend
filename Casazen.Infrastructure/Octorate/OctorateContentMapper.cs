using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Core.SiteProfile;

namespace Casazen.Infrastructure.Octorate;

/// <summary>
/// Turns the data of a property into the groups of content Octorate takes (E02/F03/US01/T02, NF-27, NF-31, NF-34, NF-37): the accommodation, the unit,
/// the descriptions in every language, the rooms with their beds, the amenities, the structured rules and the cancellation policy. The texts are read
/// only through <see cref="ListingTextResolver"/> (by <see cref="LanguageMapper"/>), so what the editor shows is what is sent. Pure: no call, no
/// database, no clock.
/// </summary>
/// <remarks>
/// <para><b>What is not sent.</b> The CIN (the field that carries it is not settled, D2); the spaces, the access and the surroundings of the listing
/// (no member of the API took them in the sandbox, so none is invented: they stay on CasaZen's own booking site); the parties and the quiet hours
/// as structured members (the same reason: the text of the rules in <c>houseRules</c> carries what the host wrote). The text helpers of the API
/// (<c>addTextToHouseRules</c>, <c>addTextToField</c>) are never used to write (NF-37).</para>
/// </remarks>
internal static class OctorateContentMapper
{
    private const string BedroomType = "BEDROOM";

    private const string LivingRoomType = "LIVING_ROOM";

    /// <summary>The accommodation: name, address, position and the hours of arrival and departure. A position of 0, 0 is not a position and is not sent.</summary>
    internal static OctoratePropertyUpdate PropertyUpdate(Property property)
    {
        ArgumentNullException.ThrowIfNull(property);
        var hasPosition = !(property.Latitude == 0m && property.Longitude == 0m);
        return new OctoratePropertyUpdate
        {
            Name = Clean(property.Name),
            Address = Clean(property.Address),
            City = Clean(property.City),
            ZipCode = Clean(property.PostalCode),
            Latitude = hasPosition ? property.Latitude : null,
            Longitude = hasPosition ? property.Longitude : null,
            CheckinStart = property.CheckInFrom?.Hour,
            CheckinEnd = property.CheckInTo?.Hour,
            Checkout = property.CheckOutBy?.Hour,
        };
    }

    /// <summary>The unit created once on a new accommodation: the headline and the description in every language, and the counters of the property.</summary>
    internal static OctorateRoomCreate RoomCreate(Property property, SitePropertyProfileDocument document, IReadOnlyList<PropertyRoom> rooms)
    {
        ArgumentNullException.ThrowIfNull(property);
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(rooms);

        var titles = LanguageMapper.LanguageMap(property, document, ListingTextField.Title);
        var beds = property.Beds is > 0 ? property.Beds.Value : rooms.Sum(room => RoomBeds.Total(room.Beds));
        return new OctorateRoomCreate
        {
            Name = UnitName(property, document),
            RateNames = titles,
            Descriptions = LanguageMapper.LanguageMap(property, document, ListingTextField.Description),
            Adults = Math.Max(property.MaxGuests, 1),
            Bedrooms = Math.Max(property.Bedrooms, 0),
            Beds = Math.Max(beds, 0),
            Bathrooms = Math.Max(property.Bathrooms, 0),
            AreaSqm = property.AreaSqm,
        };
    }

    /// <summary>The name of the unit: the Italian headline of the listing (the name of the property when the host wrote none).</summary>
    internal static string UnitName(Property property, SitePropertyProfileDocument document) =>
        ListingTextResolver.Get(property, document, ListingLanguages.Italian, ListingTextField.Title) ?? property.Name.Trim();

    /// <summary>
    /// The descriptions in every language the host wrote them in: the description, the house rules and the directions (the transport text, in Italian
    /// or else in English). A language the host did not write is not sent.
    /// </summary>
    internal static OctorateDescriptions Descriptions(Property property, SitePropertyProfileDocument document)
    {
        ArgumentNullException.ThrowIfNull(property);
        ArgumentNullException.ThrowIfNull(document);

        return new OctorateDescriptions
        {
            Description = LanguageMapper.LanguageMap(property, document, ListingTextField.Description),
            HouseRules = LanguageMapper.LanguageMap(property, document, ListingTextField.Rules),
            Directions = ListingTextResolver.Get(property, document, ListingLanguages.Italian, ListingTextField.Transport)
                         ?? ListingTextResolver.Get(property, document, ListingLanguages.English, ListingTextField.Transport),
        };
    }

    /// <summary>The rooms of the unit in the order the host gave them, numbered from 1, each with its beds and the guests they sleep.</summary>
    internal static IReadOnlyList<OctorateContentRoom> Rooms(IEnumerable<PropertyRoom> rooms)
    {
        ArgumentNullException.ThrowIfNull(rooms);

        var result = new List<OctorateContentRoom>();
        foreach (var room in rooms.OrderBy(r => r.Position).ThenBy(r => r.Id))
        {
            var beds = room.Beds
                .GroupBy(bed => BedName(bed.Type))
                .Select(group => new OctorateContentBed(group.Key, group.Sum(bed => bed.Count)))
                .OrderBy(bed => bed.Type, StringComparer.Ordinal)
                .ToList();
            var guests = room.Beds.Sum(bed => bed.Count * GuestsOf(bed.Type));
            result.Add(new OctorateContentRoom(RoomName(room.Type), result.Count + 1, Math.Max(guests, 1), beds));
        }

        return result;
    }

    /// <summary>
    /// The structured rules: children, pets, smoking and the hours. A policy the host never declared is not sent, so it stays as it is on the channel
    /// manager. A policy «on request» is not a yes: a flag cannot say it, and the text of the rules does. The rule about children is
    /// <see cref="Property.ChildrenPolicy"/> and nothing else: <see cref="Property.ChildrenWelcome"/> is the label of the public page, its own
    /// answer (<see cref="PropertyHouseRules"/>), and «not suited to children» there does not say that children are not allowed on a channel.
    /// </summary>
    internal static OctorateReservationRules ReservationRules(Property property)
    {
        ArgumentNullException.ThrowIfNull(property);

        return new OctorateReservationRules
        {
            ChildrenAllowed = property.ChildrenPolicy is { } children ? children == ChildrenPolicy.Yes : null,
            PetsAllowed = property.PetsPolicy is { } pets ? pets == PetsPolicy.Yes : null,
            SmokersAllowed = property.SmokingPolicy is { } smoking ? smoking == SmokingPolicy.Yes : null,
            CheckinTimeStart = property.CheckInFrom?.Hour,
            CheckinTimeEnd = property.CheckInTo?.Hour,
            CheckoutTime = property.CheckOutBy?.Hour,
        };
    }

    /// <summary>The cancellation policy of the property, or <c>null</c> when it declares none.</summary>
    internal static OctorateCancellationPolicy? CancellationPolicy(Property property)
    {
        ArgumentNullException.ThrowIfNull(property);

        return property.CancellationFullRefundHours is null
               && property.CancellationPartialRefundHours is null
               && property.CancellationPartialRefundPercent is null
            ? null
            : new OctorateCancellationPolicy(
                property.CancellationFullRefundHours,
                property.CancellationPartialRefundHours,
                property.CancellationPartialRefundPercent);
    }

    /// <summary>The kind of room the channel manager spells: only a bedroom and a living room are known; a studio or any other room with beds is a bedroom (the one proven value).</summary>
    internal static string RoomName(PropertyRoomType type) => type == PropertyRoomType.LivingRoom ? LivingRoomType : BedroomType;

    /// <summary>The kind of bed the channel manager spells (<c>DOUBLE</c> is the one proven on the sandbox; the others follow the same upper-case spelling).</summary>
    internal static string BedName(BedType type) => type switch
    {
        BedType.Single => "SINGLE",
        BedType.Double => "DOUBLE",
        BedType.Queen => "QUEEN",
        BedType.King => "KING",
        BedType.SofaBed => "SOFA_BED",
        BedType.Bunk => "BUNK_BED",
        BedType.Crib => "CRIB",
        _ => "DOUBLE",
    };

    /// <summary>How many guests a bed sleeps: one for a single, two for a double, queen, king, sofa bed or bunk (two berths), none for a crib.</summary>
    internal static int GuestsOf(BedType type) => type switch
    {
        BedType.Single => 1,
        BedType.Crib => 0,
        _ => 2,
    };

    private static string? Clean(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
