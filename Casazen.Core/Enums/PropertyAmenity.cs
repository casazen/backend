namespace Casazen.Core.Enums;

/// <summary>
/// Standardized property amenities for vacation rentals.
/// Based on common OTA platform amenity categories (Airbnb, Booking.com, Expedia).
/// </summary>
public enum PropertyAmenity
{
    // Connectivity
    WiFi = 1,
    TV = 2,
    CableTV = 3,
    Workspace = 4,

    // Comfort & Climate
    AirConditioning = 10,
    Heating = 11,
    Fireplace = 12,

    // Kitchen & Dining
    Kitchen = 20,
    Refrigerator = 21,
    Microwave = 22,
    Dishwasher = 23,
    CoffeeMaker = 24,
    Stove = 25,
    Oven = 26,
    DiningTable = 27,

    // Laundry
    Washer = 30,
    Dryer = 31,
    IronAndBoard = 32,

    // Outdoor & Recreation
    Pool = 40,
    HotTub = 41,
    Garden = 42,
    Patio = 43,
    Balcony = 44,
    BBQGrill = 45,
    BeachAccess = 46,

    // Parking & Transportation
    FreeParking = 50,
    PaidParking = 51,
    Garage = 52,
    EVCharger = 53,
    BikeStorage = 54,

    // Safety & Security
    SmokeDetector = 60,
    CarbonMonoxideDetector = 61,
    FireExtinguisher = 62,
    FirstAidKit = 63,
    SecurityCameras = 64,
    Safe = 65,

    // Accessibility
    WheelchairAccessible = 70,
    Elevator = 71,
    StepFreeAccess = 72,

    // Family & Lifestyle
    PetFriendly = 80,
    SmokingAllowed = 81,
    Crib = 82,
    HighChair = 83,

    // Gym & Wellness
    Gym = 90,
    Sauna = 91,

    // Miscellaneous
    SelfCheckIn = 100,
    LongTermStaysAllowed = 101,
    LuggageDropOffAllowed = 102,
    PrivateEntrance = 103
}
