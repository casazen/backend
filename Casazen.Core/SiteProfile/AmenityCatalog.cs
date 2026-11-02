using Casazen.Core.Enums;

namespace Casazen.Core.SiteProfile;

/// <summary>A group of the amenity list of the property page («Tutti i servizi»): the five of the design of the booking site.</summary>
/// <param name="Id">Stable text: <c>comfort</c>, <c>kitchen</c>, <c>bedbath</c>, <c>outdoor</c>, <c>safety</c>.</param>
public sealed record AmenityGroup(string Id, string LabelIt, string LabelEn);

/// <summary>How the booking site names and draws one <see cref="PropertyAmenity"/>.</summary>
/// <param name="Amenity">The amenity: its name is what the public DTOs carry in <c>amenities</c>.</param>
/// <param name="Icon">An icon of the booking site's icon set.</param>
/// <param name="Group">The <see cref="AmenityGroup.Id"/> it is listed under.</param>
public sealed record AmenityCatalogEntry(PropertyAmenity Amenity, string Icon, string Group, string LabelIt, string LabelEn)
{
    /// <summary>What the public DTOs carry: the name of the enum member (<c>WiFi</c>, <c>Pool</c>...).</summary>
    public string Name => Amenity.ToString();
}

/// <summary>
/// The reference data of the amenities of a property (DB-11a): for each <see cref="PropertyAmenity"/> the label in Italian and English, the
/// icon and the group the property page lists it under, so the page never shows a raw code and never guesses. The labels say what the
/// amenity says and no more (a «Piscina», not a «Piscina privata»: the host's data do not tell whether it is shared). Every member of the
/// enum has exactly one entry — <c>AmenityCatalogTests</c> fails the build when a new amenity is added without one. Served as is by
/// <c>GET api/public/amenities</c>.
/// </summary>
public static class AmenityCatalog
{
    private const string Comfort = "comfort";
    private const string Kitchen = "kitchen";
    private const string BedBath = "bedbath";
    private const string Outdoor = "outdoor";
    private const string Safety = "safety";

    public static IReadOnlyList<AmenityGroup> Groups { get; } =
    [
        new(Comfort, "Comfort", "Comfort"),
        new(Kitchen, "Cucina", "Kitchen"),
        new(BedBath, "Camera e bagno", "Bedroom and bathroom"),
        new(Outdoor, "Esterni e posizione", "Outdoors and location"),
        new(Safety, "Sicurezza e arrivo", "Safety and arrival"),
    ];

    public static IReadOnlyList<AmenityCatalogEntry> Entries { get; } =
    [
        // Comfort (connectivity, climate, laundry, access)
        new(PropertyAmenity.WiFi, "wifi", Comfort, "Wi-Fi", "Wi-Fi"),
        new(PropertyAmenity.TV, "tv", Comfort, "TV", "TV"),
        new(PropertyAmenity.CableTV, "tv", Comfort, "TV via cavo o satellite", "Cable or satellite TV"),
        new(PropertyAmenity.Workspace, "monitor", Comfort, "Spazio per lavorare", "Workspace"),
        new(PropertyAmenity.AirConditioning, "snow", Comfort, "Aria condizionata", "Air conditioning"),
        new(PropertyAmenity.Heating, "thermometer", Comfort, "Riscaldamento", "Heating"),
        new(PropertyAmenity.Fireplace, "flame", Comfort, "Camino", "Fireplace"),
        new(PropertyAmenity.Washer, "shirt", Comfort, "Lavatrice", "Washing machine"),
        new(PropertyAmenity.Dryer, "shirt", Comfort, "Asciugatrice", "Tumble dryer"),
        new(PropertyAmenity.IronAndBoard, "shirt", Comfort, "Ferro e asse da stiro", "Iron and ironing board"),
        new(PropertyAmenity.Elevator, "elevator", Comfort, "Ascensore", "Lift"),
        new(PropertyAmenity.WheelchairAccessible, "user-round", Comfort, "Accessibile in sedia a rotelle", "Wheelchair accessible"),
        new(PropertyAmenity.StepFreeAccess, "door", Comfort, "Ingresso senza gradini", "Step-free access"),
        new(PropertyAmenity.Gym, "zap", Comfort, "Palestra", "Gym"),
        new(PropertyAmenity.Sauna, "thermometer", Comfort, "Sauna", "Sauna"),
        new(PropertyAmenity.LongTermStaysAllowed, "calendar", Comfort, "Soggiorni lunghi ammessi", "Long stays allowed"),

        // Kitchen
        new(PropertyAmenity.Kitchen, "utensils", Kitchen, "Cucina", "Kitchen"),
        new(PropertyAmenity.Refrigerator, "thermometer", Kitchen, "Frigorifero", "Fridge"),
        new(PropertyAmenity.Microwave, "flame", Kitchen, "Forno a microonde", "Microwave"),
        new(PropertyAmenity.Dishwasher, "sparkles", Kitchen, "Lavastoviglie", "Dishwasher"),
        new(PropertyAmenity.CoffeeMaker, "coffee", Kitchen, "Macchina del caffè", "Coffee machine"),
        new(PropertyAmenity.Stove, "flame", Kitchen, "Piano cottura", "Hob"),
        new(PropertyAmenity.Oven, "flame", Kitchen, "Forno", "Oven"),
        new(PropertyAmenity.DiningTable, "utensils", Kitchen, "Tavolo da pranzo", "Dining table"),

        // Bedroom and bathroom
        new(PropertyAmenity.Crib, "bed", BedBath, "Culla", "Cot"),
        new(PropertyAmenity.HighChair, "smile", BedBath, "Seggiolone", "High chair"),

        // Outdoors and location (outdoor spaces, parking, the rule about pets and smoking)
        new(PropertyAmenity.Pool, "waves", Outdoor, "Piscina", "Pool"),
        new(PropertyAmenity.HotTub, "droplet", Outdoor, "Vasca idromassaggio", "Hot tub"),
        new(PropertyAmenity.Garden, "trees", Outdoor, "Giardino", "Garden"),
        new(PropertyAmenity.Patio, "sun", Outdoor, "Patio", "Patio"),
        new(PropertyAmenity.Balcony, "sun", Outdoor, "Balcone", "Balcony"),
        new(PropertyAmenity.Terrace, "sun", Outdoor, "Terrazza", "Terrace"),
        new(PropertyAmenity.BBQGrill, "flame", Outdoor, "Barbecue", "Barbecue"),
        new(PropertyAmenity.BeachAccess, "umbrella", Outdoor, "Accesso alla spiaggia", "Beach access"),
        new(PropertyAmenity.FreeParking, "parking", Outdoor, "Parcheggio gratuito", "Free parking"),
        new(PropertyAmenity.PaidParking, "parking", Outdoor, "Parcheggio a pagamento", "Paid parking"),
        new(PropertyAmenity.Garage, "parking", Outdoor, "Garage", "Garage"),
        new(PropertyAmenity.EVCharger, "zap", Outdoor, "Ricarica per auto elettriche", "EV charger"),
        new(PropertyAmenity.BikeStorage, "lock", Outdoor, "Deposito bici", "Bike storage"),
        new(PropertyAmenity.PetFriendly, "paw", Outdoor, "Animali ammessi", "Pets allowed"),
        new(PropertyAmenity.SmokingAllowed, "alert-circle", Outdoor, "Si può fumare", "Smoking allowed"),

        // Safety and arrival
        new(PropertyAmenity.SmokeDetector, "alert-circle", Safety, "Rilevatore di fumo", "Smoke detector"),
        new(PropertyAmenity.CarbonMonoxideDetector, "alert-circle", Safety, "Rilevatore di monossido di carbonio", "Carbon monoxide detector"),
        new(PropertyAmenity.FireExtinguisher, "shield", Safety, "Estintore", "Fire extinguisher"),
        new(PropertyAmenity.FirstAidKit, "cross", Safety, "Kit di primo soccorso", "First-aid kit"),
        new(PropertyAmenity.SecurityCameras, "shield", Safety, "Telecamere di sicurezza", "Security cameras"),
        new(PropertyAmenity.Safe, "lock", Safety, "Cassaforte", "Safe"),
        new(PropertyAmenity.SelfCheckIn, "key", Safety, "Check-in autonomo", "Self check-in"),
        new(PropertyAmenity.PrivateEntrance, "door", Safety, "Ingresso indipendente", "Private entrance"),
        new(PropertyAmenity.LuggageDropOffAllowed, "shopping-bag", Safety, "Deposito bagagli", "Luggage drop-off"),
    ];
}
