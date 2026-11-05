using Casazen.Core.Enums;

namespace Casazen.Infrastructure.Octorate;

/// <summary>
/// The amenity of the channel manager (and its codes on Airbnb and Booking.com) that stands for one <see cref="PropertyAmenity"/> of CasaZen
/// (NF-33, AC-NF33-2).
/// </summary>
/// <param name="Amenity">The amenity of the CasaZen catalog (<c>AmenityCatalog</c>).</param>
/// <param name="OctorateId">
/// The <c>id</c> of the amenity in <c>GET /rest/v1/meta/octorateAmenities</c>; <c>null</c> when that catalog has no amenity for it (see
/// <see cref="AmenityCatalogMapper.NotInTheCatalog"/>): the amenity stays on CasaZen and nothing is sent for it.
/// </param>
/// <param name="OctorateName">The <c>internalName</c> of the same amenity (the name <c>meta/content</c> calls <c>octorateName</c>).</param>
/// <param name="AirbnbCode">
/// The value the amenity carries for the portal <c>airbnb_xml</c> (<see cref="AmenityCatalogMapper.AirbnbPortal"/>); <c>null</c> = the
/// channel manager has none for it, so the amenity does not reach Airbnb through it.
/// </param>
/// <param name="BookingCode">
/// The value the amenity carries for the portal <c>booking_xml</c> (<see cref="AmenityCatalogMapper.BookingPortal"/>); <c>null</c> = none.
/// </param>
public sealed record AmenityMapping(PropertyAmenity Amenity, long? OctorateId, string? OctorateName, string? AirbnbCode, string? BookingCode);

/// <summary>
/// Maps every <see cref="PropertyAmenity"/> of the CasaZen catalog to the amenity of the channel manager and to the code of Airbnb and
/// Booking.com (NF-33, ADR-035). Expedia has no mapping: its content stays on the channel. The labels and the search by word are those of the
/// catalog (<c>AmenityCatalog</c>, <c>GET api/public/amenities</c>), not of this class.
/// </summary>
/// <remarks>
/// <para><b>Where the codes come from.</b> From the example response of <c>GET /rest/v1/meta/octorateAmenities</c> in the Postman collection of the
/// channel manager (downloaded on 2026-10-31: 307 amenities, each with the value it has for every portal that takes it). The real catalog
/// answered 424 amenities on the sandbox (2026-11-02), but that run kept the shape of the answer and not its values. So the codes below are the
/// published ones and have <b>not</b> been read again on the sandbox: <see cref="ConfirmedOnSandbox"/> stays <c>false</c> until
/// <c>GET meta/octorateAmenities</c> and <c>GET meta/content?portal=airbnb_xml|booking_xml&amp;category=ROOM_AMENITY</c> have been compared with this
/// table, and the push of the amenities must not run before that.</para>
/// <para><b>How an amenity was chosen.</b> The amenity of the channel manager that says what the CasaZen one says and that carries a value for the
/// portals; where the closest one says a little more or less than CasaZen does, the choice is noted on the row. A code that is
/// <c>null</c> is a portal the channel manager has no value for on that amenity (not a guess). Three amenities have no counterpart in the
/// catalog at all.</para>
/// </remarks>
public static class AmenityCatalogMapper
{
    /// <summary>The portal of Airbnb in the channel manager.</summary>
    public const string AirbnbPortal = "airbnb_xml";

    /// <summary>The portal of Booking.com in the channel manager.</summary>
    public const string BookingPortal = "booking_xml";

    /// <summary>
    /// Whether the codes of <see cref="Entries"/> were compared with the answer of the sandbox. <c>false</c>: they are the published ones; the
    /// push of the amenities waits for the comparison.
    /// </summary>
    public const bool ConfirmedOnSandbox = false;

    /// <summary>One entry for every <see cref="PropertyAmenity"/>, in the order of the catalog.</summary>
    public static IReadOnlyList<AmenityMapping> Entries { get; } =
    [
        // Connectivity
        Map(PropertyAmenity.WiFi, 841, "Wireless internet connection", "wireless_internet", "123"),
        Map(PropertyAmenity.TV, 842, "tv", "tv", "5075"), // «Flat TV» for the channel manager
        Map(PropertyAmenity.CableTV, 836, "Cable television", null, "18"),
        Map(PropertyAmenity.Workspace, 395, "LAPTOP WORKSPACE", "laptop_friendly_workspace", null),

        // Comfort and climate
        Map(PropertyAmenity.AirConditioning, 824, "Air Conditioning", "ac", "2"),
        Map(PropertyAmenity.Heating, 38, "CENTRAL HEATING", "heating", "166"), // the only heating with a value for the portals
        Map(PropertyAmenity.Fireplace, 125, "FIREPLACE", "fireplace", "5071"),

        // Kitchen and dining
        Map(PropertyAmenity.Kitchen, 837, "Kitchen", "kitchen", "59"),
        Map(PropertyAmenity.Refrigerator, 70, "FRIDGE / FREEZER", null, "5022"),
        Map(PropertyAmenity.Microwave, 66, "MICROWAVE", "microwave", "5032"),
        Map(PropertyAmenity.Dishwasher, 854, "DISH_WASHER", "dishwasher", "32"),
        Map(PropertyAmenity.CoffeeMaker, 73, "COFFEE MAKER", "coffee_maker", "5120"),
        Map(PropertyAmenity.Stove, 109, "STOVE", "stove", "5097"),
        Map(PropertyAmenity.Oven, 63, "OVEN", "oven", "5096"),
        Map(PropertyAmenity.DiningTable, 108, "DINING TABLE", null, "5126"),

        // Laundry
        Map(PropertyAmenity.Washer, 830, "WASHING MACHINE", "washer", "5034"),
        Map(PropertyAmenity.Dryer, 913, "Dryer on property", null, null),
        Map(PropertyAmenity.IronAndBoard, 165, "IRON", "iron", "55"), // the iron; the ironing board (166) has a value for Booking.com only

        // Outdoor and recreation
        Map(PropertyAmenity.Pool, 382, "PRIVATE POOL", "pool", "5093"), // the only pool with a value for both portals; the catalog calls it private
        Map(PropertyAmenity.HotTub, 131, "HOT TUB", "jacuzzi", "5014"),
        Map(PropertyAmenity.Garden, 510, "PRIVATE_YARD", null, null), // the closest to a garden; no value for the portals
        Map(PropertyAmenity.Patio, 461, "PATIO", null, "5037"),
        Map(PropertyAmenity.Balcony, 59, "BALCONY", "patio_or_belcony", "5017"), // the spelling is the catalog's
        Map(PropertyAmenity.BBQGrill, 25, "BBQ barbeque", "bbq_area", null), // the catalog has a second barbecue (27) with a value for Booking.com only
        Map(PropertyAmenity.BeachAccess, 893, "CM_AMN_BEACH_ACCESS", null, null),
        Map(PropertyAmenity.Terrace, 605, "TERRACE", null, "5123"), // «Private Terrace» for the channel manager

        // Parking and transportation
        Map(PropertyAmenity.FreeParking, 271, "FREE PARKING", "free_parking", null),
        Map(PropertyAmenity.PaidParking, 370, "PAID PARKING ON THE STREET", "paid_parking", null), // «paid parking nearby»: it claims no more than that
        Map(PropertyAmenity.Garage, 984, "INDOOR_PARKING", null, null),
        Map(PropertyAmenity.EVCharger, 536, "EV_CHARGER", "ev_charger", null),
        Map(PropertyAmenity.BikeStorage, null, null, null, null),

        // Safety and security
        Map(PropertyAmenity.SmokeDetector, 350, "SMOKE DETECTORS", "smoke_detector", null),
        Map(PropertyAmenity.CarbonMonoxideDetector, 542, "CARBON_MONOXIDE_DETECTOR", "carbon_monoxide_detector", null),
        Map(PropertyAmenity.FireExtinguisher, 541, "FIRE_EXTINGUISHER", "fire_extinguisher", "5202"),
        Map(PropertyAmenity.FirstAidKit, 543, "FIRST_AID_KIT", "first_aid_kit", null),
        Map(PropertyAmenity.SecurityCameras, 932, "Security system", null, null), // the closest: a security system, not cameras only
        Map(PropertyAmenity.Safe, 175, "SAFE", null, "92"),

        // Accessibility
        Map(PropertyAmenity.WheelchairAccessible, 985, "WHEELCHAIR_ACCESS", null, null),
        Map(PropertyAmenity.Elevator, 616, "ELEVATOR", null, null),
        Map(PropertyAmenity.StepFreeAccess, null, null, null, null), // the catalog has the step-free access of a room, not of the entrance

        // Family and lifestyle
        Map(PropertyAmenity.PetFriendly, 896, "CM_AMN_PETS_ALLOWED", null, null), // on the portals pets are a rule of the stay, not an amenity
        Map(PropertyAmenity.SmokingAllowed, null, null, null, null),
        Map(PropertyAmenity.Crib, 244, "CRIBS AVAILABLE", "crib", "5175"),
        Map(PropertyAmenity.HighChair, 95, "HIGH CHAIRS", "high_chair", "5127"),

        // Gym and wellness
        Map(PropertyAmenity.Gym, 400, "GYM", "gym", null),
        Map(PropertyAmenity.Sauna, 931, "Sauna (common area)", null, null), // the catalog has a private sauna too (895): the shared one claims less

        // Miscellaneous
        Map(PropertyAmenity.SelfCheckIn, 866, "SELF_CHECKIN", null, null),
        Map(PropertyAmenity.LongTermStaysAllowed, 539, "LONG_TERM_STAYS_ALLOWED", "long_term_stays_allowed", null),
        Map(PropertyAmenity.LuggageDropOffAllowed, 156, "LUGGAGE STORAGE FACILITIES", "luggage_dropoff_allowed", null),
        Map(PropertyAmenity.PrivateEntrance, 529, "PRIVATE_ENTRANCE", null, "5076"),
    ];

    private static readonly Dictionary<PropertyAmenity, AmenityMapping> ByAmenity = Entries.ToDictionary(entry => entry.Amenity);

    /// <summary>
    /// The amenities the catalog of the channel manager has no counterpart for: they stay on CasaZen, and the push sends nothing for them. Each
    /// is a decision to take again when the sandbox has been read (the catalog there is larger than the published example).
    /// </summary>
    public static IReadOnlyList<PropertyAmenity> NotInTheCatalog { get; } =
        [.. Entries.Where(entry => entry.OctorateId is null).Select(entry => entry.Amenity)];

    /// <summary>The entry of an amenity; every <see cref="PropertyAmenity"/> has one.</summary>
    public static AmenityMapping Get(PropertyAmenity amenity) =>
        ByAmenity.TryGetValue(amenity, out var entry)
            ? entry
            : throw new ArgumentOutOfRangeException(nameof(amenity), amenity, "The amenity has no entry in the table of the channel manager.");

    /// <summary>
    /// The ids of the channel manager for the amenities of a property, each once, in the order of the catalog; the amenities with no
    /// counterpart (<see cref="NotInTheCatalog"/>) are left out.
    /// </summary>
    public static IReadOnlyList<long> OctorateIds(IEnumerable<PropertyAmenity> amenities)
    {
        ArgumentNullException.ThrowIfNull(amenities);

        var wanted = amenities.ToHashSet();
        return [.. Entries.Where(entry => wanted.Contains(entry.Amenity) && entry.OctorateId is not null).Select(entry => entry.OctorateId!.Value).Distinct()];
    }

    private static AmenityMapping Map(PropertyAmenity amenity, long? octorateId, string? octorateName, string? airbnbCode, string? bookingCode) =>
        new(amenity, octorateId, octorateName, airbnbCode, bookingCode);
}
